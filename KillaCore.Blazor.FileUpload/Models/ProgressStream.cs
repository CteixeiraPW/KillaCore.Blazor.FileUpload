namespace KillaCore.Blazor.FileUpload.Models;

internal class ProgressStream(Stream inner, Action<long> onProgress) : Stream
{
    private long _totalRead;
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        _totalRead += read;
        onProgress(_totalRead);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        _totalRead += read;
        onProgress(_totalRead);
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void SetLength(long value) => inner.SetLength(value);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
}