using KillaCore.Blazor.FileUpload.Client.Models;
namespace KillaCore.Blazor.FileUpload.Services;

public interface IFileUploadServerHooks
{
    /// <summary>
    /// Returns true when this content is already held in permanent storage, in which case
    /// <see cref="SaveFileAsync"/> is skipped and the file is reported to the UI as
    /// <see cref="TransferStatus.Skipped"/> rather than as a failure.
    /// <para>
    /// The server-computed hash is on <c>data.DetectedHash</c>. When returning true you may set
    /// <c>data.FinalResourceId</c> and <c>data.Metadata</c> to describe the copy that already
    /// exists, so the UI can link the user to it.
    /// </para>
    /// </summary>
    Task<bool> CheckRemoteDuplicateAsync(FileTransferData data, CancellationToken ct);

    Task SaveFileAsync(FileTransferData data, Stream fileStream, CancellationToken ct);

    /// <summary>
    /// Fires once the whole batch has finished. <paramref name="files"/> contains every transfer
    /// in the batch — successes, failures and skips alike — each carrying its own
    /// <see cref="FileTransferData.Status"/>, so a wholly-failed batch is reported too.
    /// </summary>
    Task OnBatchCompletedAsync(string batchId, IReadOnlyList<FileTransferData> files);
}
