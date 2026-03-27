using System.Collections.Concurrent;
using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Services;

namespace KillaCore.Blazor.FileUpload.Test.Services;

// This class lives in the consuming developer's application, NOT your NuGet package.
public class ApplicationUploadHooks(
    IWebHostEnvironment env,
    ILogger<ApplicationUploadHooks> logger) : IFileUploadServerHooks
{
    private readonly string _finalStoragePath = Path.Combine(env.ContentRootPath, "SecureUploads");

    // FAKE DATABASE: We use ConcurrentBag because multiple files upload and save simultaneously
    private static readonly ConcurrentBag<FileMetadataEntity> _fakeDatabase = [];

    /// <summary>
    /// Checks the fake database to see if this exact file has already been uploaded previously.
    /// </summary>
    public Task<bool> CheckRemoteDuplicateAsync(string detectedHash, CancellationToken ct)
    {
        // Query the in-memory list
        bool exists = _fakeDatabase.Any(f => f.FileHash == detectedHash);

        if (exists)
        {
            logger.LogInformation("Duplicate upload prevented. Hash: {Hash}", detectedHash);
        }

        // Return synchronously as Task.FromResult since we aren't awaiting a real DB call
        return Task.FromResult(exists);
    }

    /// <summary>
    /// Moves the file from your package's Temp folder to the application's final permanent storage,
    /// and records the metadata in the fake database.
    /// </summary>
    public async Task SaveFileAsync(FileTransferData data, Stream fileStream, CancellationToken ct)
    {
        // 1. Ensure the final directory exists
        Directory.CreateDirectory(_finalStoragePath);

        // 2. Generate a secure, collision-free file name for the disk
        string secureFileName = $"{Guid.NewGuid():N}{Path.GetExtension(data.FileName)}";
        string destinationPath = Path.Combine(_finalStoragePath, secureFileName);

        try
        {
            // 3. Stream the file to the permanent disk
            await using var fs = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await fileStream.CopyToAsync(fs, ct);

            // 4. Save the metadata to the static list
            var newRecord = new FileMetadataEntity
            {
                OriginalName = data.FileName,
                PhysicalName = secureFileName,
                MimeType = data.MimeType,
                SizeBytes = data.FileSize,
                FileHash = data.DetectedHash,
                BatchId = data.BatchId,
                UploadedAt = DateTime.UtcNow
            };

            _fakeDatabase.Add(newRecord);

            // 5. Update the package's model so the UI knows where it landed
            data.FinalResourceId = newRecord.Id.ToString();

            logger.LogInformation("Successfully saved file {Name} to {Path}. Fake DB Total: {Count}",
                data.FileName, destinationPath, _fakeDatabase.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save file {Name}", data.FileName);

            // Clean up the partial file if something fails
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Fires when the entire batch of files has finished processing.
    /// </summary>
    public Task OnBatchCompletedAsync(string batchId, IReadOnlyList<FileTransferData> files)
    {
        int successCount = files.Count(f => f.Status == TransferStatus.Completed);
        int failedCount = files.Count(f => f.Status == TransferStatus.Failed);

        logger.LogInformation("Batch {BatchId} completed. {Success} saved, {Failed} failed. Total files in system history: {TotalDb}",
            batchId, successCount, failedCount, _fakeDatabase.Count);

        return Task.CompletedTask;
    }
}

// Example Data Model
public class FileMetadataEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OriginalName { get; set; } = string.Empty;
    public string PhysicalName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? FileHash { get; set; }
    public string BatchId { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
}