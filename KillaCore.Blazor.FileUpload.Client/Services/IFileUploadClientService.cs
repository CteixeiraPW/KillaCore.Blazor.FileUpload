using KillaCore.Blazor.FileUpload.Client.Models;

namespace KillaCore.Blazor.FileUpload.Client.Services;

public interface IFileUploadClientService
{
    // Asks the server to encrypt the rules (e.g., AllowedMimeTypes)
    Task<string> GetPolicyTokenAsync(FileProcessingOptions options, CancellationToken ct = default);

    // Asks the server for a specific file upload token
    Task<string> GetUploadTokenAsync(string fileId, string userId, CancellationToken ct = default);

    Task NotifyBatchCompletedAsync(string batchId, IEnumerable<FileTransferData> files, CancellationToken ct = default);
}