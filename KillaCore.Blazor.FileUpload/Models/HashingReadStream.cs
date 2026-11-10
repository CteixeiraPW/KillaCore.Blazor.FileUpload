using System.Security.Cryptography;

namespace KillaCore.Blazor.FileUpload.Models;

/// <summary>
/// Thrown when a stream carries more bytes than the limit in force. Raised mid-copy so an
/// over-sized file stops being written the moment it is detected, instead of being written in
/// full and rejected afterwards.
/// </summary>
internal sealed class UploadTooLargeException(long limitBytes)
    : Exception($"File exceeds the {limitBytes:N0} byte limit for this upload.")
{
    public long LimitBytes { get; } = limitBytes;
}

/// <summary>
/// A forward-only stream handed to <c>IFileUploadServerHooks.SaveFileAsync</c> in direct-to-storage
/// mode. It replays the bytes already consumed for content inspection, then continues from the
/// request body, hashing and counting as the hook reads and refusing to go past the size limit.
/// <para>
/// This is what makes the single-write path possible: the SHA-256 is a by-product of the hook's
/// own copy rather than a separate pass over a temp file. The cost is that neither the hash nor
/// the length exists until the hook has drained the stream.
/// </para>
/// </summary>
internal sealed class HashingReadStream(
    ReadOnlyMemory<byte> head,
    Stream inner,
    long maxBytes) : Stream
{
    private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private int _headOffset;
    private long _totalRead;
    private string? _computedHash;

    /// <summary>Bytes seen so far; final once the stream has been read to its end.</summary>
    public long TotalBytesRead => _totalRead;

    /// <summary>
    /// Hex SHA-256 of everything read. Only meaningful once the stream is drained; calling it
    /// finalises the hash, so it is computed once and cached.
    /// </summary>
    public string ComputedHash =>
        _computedHash ??= Convert.ToHexString(_hasher.GetHashAndReset());

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;

        if (_headOffset < head.Length)
        {
            // Serve the inspected prefix first, so the hook sees the complete file.
            read = Math.Min(buffer.Length, head.Length - _headOffset);
            head.Slice(_headOffset, read).CopyTo(buffer);
            _headOffset += read;
        }
        else
        {
            read = await inner.ReadAsync(buffer, cancellationToken);
        }

        if (read <= 0)
            return 0;

        Account(buffer.Span[..read]);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read;

        if (_headOffset < head.Length)
        {
            read = Math.Min(count, head.Length - _headOffset);
            head.Slice(_headOffset, read).CopyTo(buffer.AsMemory(offset, read));
            _headOffset += read;
        }
        else
        {
            read = inner.Read(buffer, offset, count);
        }

        if (read <= 0)
            return 0;

        Account(buffer.AsSpan(offset, read));
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(ReadOnlySpan<byte> chunk)
    {
        _totalRead += chunk.Length;

        if (_totalRead > maxBytes)
            throw new UploadTooLargeException(maxBytes);

        _hasher.AppendData(chunk);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hasher.Dispose();
        base.Dispose(disposing);
    }

    // --- Forward-only: the request body cannot be rewound. ---
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("Length is unknown until the stream is drained.");
    public override long Position
    {
        get => _totalRead;
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
