using System.Text.Json.Serialization;

namespace KillaCore.Blazor.FileUpload.Client.Models;

public sealed class FileTransferData : IDisposable
{
    // --- Standard Properties ---
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int Index { get; set; }
    public string BatchId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; } = 0;
    public string MimeType { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = [];

    // --- State ---
    [JsonIgnore]
    public TransferStage Stage { get; set; } = TransferStage.Pending;
    public TransferStatus Status { get; set; } = TransferStatus.Pending;
    public string StatusMessage { get; set; } = "Queued";

    // "Local" Progress (0-100) for the current active stage
    [JsonIgnore]
    public double StageProgressPercent { get; set; }

    // --- Artifacts ---
    public string? UploadToken { get; set; }
    public string? DetectedHash { get; set; }
    public string? FinalResourceId { get; set; }

    // --- Cancellation/UI ---
    public DateTime LastUIUpdate { get; set; } = DateTime.MinValue;
    public DateTime StartTime { get; set; } // [NEW] Good for calculating speed
    public DateTime? EndTime { get; set; }  // [NEW] Good for analytics

    [JsonIgnore]
    public CancellationTokenSource IndividualCts { get; set; } = new();

    // --- Construstors & Init ---
    public FileTransferData()
    {
    }

    public FileTransferData(string fileName, long fileSize, string mimeType)
    {
        FileName = fileName;
        FileSize = fileSize;
        MimeType = mimeType;
    }

    public bool IsFinished => Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled or TransferStatus.Skipped;

    // --- The Logic ---
    public double LifecyclePercent
    {
        get
        {
            if (Status == TransferStatus.Completed || Status == TransferStatus.Skipped) return 100;
            if (Status == TransferStatus.Pending) return 0;

            // For Failed/Cancelled, leave the bar exactly where it died during upload
            if (Status == TransferStatus.Cancelled || Status == TransferStatus.Failed)
            {
                return StageProgressPercent;
            }

            // During Uploading, just use the raw network percentage (0 to 100)
            if (Stage == TransferStage.Uploading)
            {
                return StageProgressPercent;
            }

            return 0;
        }
    }

    public void Dispose()
    {
        // Check for null just in case, though it's initialized inline above
        IndividualCts?.Dispose();

        // Suppress finalization (good practice, though strictly not needed 
        // unless you have a finalizer, which you don't need here)
        GC.SuppressFinalize(this);
    }
}