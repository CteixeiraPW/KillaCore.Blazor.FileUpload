using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// The default quarantine: a folder on the server's own disk, configured by <c>TempFolder</c>.
/// <para>
/// Fine for most applications and the fastest option when the disk is local, because promoting a
/// file to permanent storage is a plain read. Replace it when scratch space is the constraint —
/// see <see cref="IUploadQuarantineStore"/>.
/// </para>
/// </summary>
internal sealed class LocalDiskQuarantineStore(
    IOptions<FileUploadServerOptions> options,
    ILogger<LocalDiskQuarantineStore> logger) : IUploadQuarantineStore
{
    private const string FallbackFolderName = "KillaCoreUploads";

    private string ResolveRoot()
    {
        string folderConfig = options.Value.TempFolder;
        if (string.IsNullOrWhiteSpace(folderConfig)) folderConfig = FallbackFolderName;

        return Path.IsPathRooted(folderConfig)
            ? folderConfig
            : Path.Combine(Path.GetTempPath(), folderConfig);
    }

    public async Task<string> WriteAsync(FileTransferData data, Stream content, CancellationToken ct)
    {
        var root = ResolveRoot();
        Directory.CreateDirectory(root);

        var path = Path.Combine(root, $"{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var fs = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(fs, ct);
            }
        }
        catch
        {
            // An over-sized or cancelled upload aborts mid-copy. The caller has no handle to clean
            // up with, so a partial entry would sit here until the janitor noticed it an hour later.
            TryDelete(path);
            throw;
        }

        return path;
    }

    public Task<Stream> OpenReadAsync(string handle, CancellationToken ct)
    {
        Stream stream = new FileStream(
            handle, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string handle, CancellationToken ct)
    {
        TryDelete(handle);
        return Task.CompletedTask;
    }

    public Task SweepOrphansAsync(TimeSpan maxAge, CancellationToken ct)
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root)) return Task.CompletedTask;

        int deleted = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.tmp"))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);

            // Older than the cut-off means no request is still writing it: the process died or
            // was recycled mid-upload.
            if (DateTime.UtcNow - info.LastWriteTimeUtc <= maxAge) continue;

            if (TryDelete(file)) deleted++;
        }

        if (deleted > 0 && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("KillaCore quarantine sweep removed {Count} orphaned file(s).", deleted);

        return Task.CompletedTask;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // Locked by anti-virus or another handle; the next sweep will try again.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
