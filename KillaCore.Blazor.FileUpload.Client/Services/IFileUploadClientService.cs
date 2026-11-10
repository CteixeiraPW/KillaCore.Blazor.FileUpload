using KillaCore.Blazor.FileUpload.Client.Models;

namespace KillaCore.Blazor.FileUpload.Client.Services;

/// <summary>
/// The non-file half of the pipeline: everything that travels as ordinary JSON rather than as
/// multipart form data.
/// <para>
/// Every method takes the <see cref="FileProcessingOptions"/> in force so it can resolve its
/// route through <see cref="FileProcessingOptions.ResolveApiUrl"/>. That is not decoration: the
/// upload token and the encrypted policy are signed by one server and verified by the same one,
/// so a deployment that uploads to <c>ServerBaseUrl</c> while fetching its credentials from the
/// page's own origin fails on mismatched keys. One option decides the destination of all of it.
/// </para>
/// </summary>
public interface IFileUploadClientService
{
    /// <summary>
    /// Asks the server which limits apply to <c>options.UploadContext</c>, so the component can
    /// pre-filter against the real rules. Returns null when the limits could not be retrieved —
    /// the caller then falls back to its local values, since the server enforces regardless.
    /// </summary>
    Task<UploadLimits?> GetEffectiveLimitsAsync(FileProcessingOptions options, CancellationToken ct = default);

    // Asks the server for a specific file upload token
    Task<string> GetUploadTokenAsync(FileProcessingOptions options, string fileId, string userId, CancellationToken ct = default);

    Task NotifyBatchCompletedAsync(FileProcessingOptions options, string batchId, IEnumerable<FileTransferData> files, CancellationToken ct = default);
}
