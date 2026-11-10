using KillaCore.Blazor.FileUpload.Client.Models;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// Holds an upload between arrival and acceptance.
/// <para>
/// Nothing reaches permanent storage until the content has been hashed and checked, which means
/// the bytes have to rest somewhere in between. That somewhere defaults to local disk, but local
/// disk does not scale: at peak a server needs <i>concurrent users × MaxConcurrentUploads × file
/// size</i> of scratch space, which is tens of gigabytes for a handful of people uploading large
/// files, on a container that may have two. Implement this over S3, Azure Blob or a network share
/// to move that pressure off the machine.
/// </para>
/// <para>
/// This is also where a file sits while it is scanned, so a virus checker examines the quarantine
/// copy and an infected file never becomes reachable.
/// </para>
/// </summary>
public interface IUploadQuarantineStore
{
    /// <summary>
    /// Consumes <paramref name="content"/> and returns an opaque handle for reading it back.
    /// <para>
    /// Read the stream exactly once and to its end: it is the live request body, and the caller
    /// takes the file's size and SHA-256 from how much of it you consumed.
    /// </para>
    /// </summary>
    Task<string> WriteAsync(FileTransferData data, Stream content, CancellationToken ct);

    /// <summary>Opens the quarantined content for reading. The caller disposes the stream.</summary>
    Task<Stream> OpenReadAsync(string handle, CancellationToken ct);

    /// <summary>
    /// Discards the quarantined content. Called in a finally block, including after a failure, so
    /// it should not throw for a handle that is already gone.
    /// </summary>
    Task DeleteAsync(string handle, CancellationToken ct);

    /// <summary>
    /// Removes entries abandoned by a hard crash or a recycled process, older than
    /// <paramref name="maxAge"/>. Called periodically by the background janitor.
    /// <para>
    /// Does nothing by default: a store whose backing service expires objects on its own — an S3
    /// lifecycle rule, say — has nothing to sweep.
    /// </para>
    /// </summary>
    Task SweepOrphansAsync(TimeSpan maxAge, CancellationToken ct) => Task.CompletedTask;
}
