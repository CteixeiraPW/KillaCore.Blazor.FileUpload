using KillaCore.Blazor.FileUpload.Client.Models;
namespace KillaCore.Blazor.FileUpload.Services;

public interface IFileUploadServerHooks
{
    Task<bool> CheckRemoteDuplicateAsync(string detectedHash, CancellationToken ct);
    Task SaveFileAsync(FileTransferData data, Stream fileStream, CancellationToken ct);
    Task OnBatchCompletedAsync(string batchId, IReadOnlyList<FileTransferData> files);
}