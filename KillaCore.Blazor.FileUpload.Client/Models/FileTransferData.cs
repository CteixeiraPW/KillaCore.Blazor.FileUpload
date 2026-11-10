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
    public Dictionary<string, string?> Metadata { get; set; } = [];

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

    /// <summary>
    /// Where the quarantine store is holding this file while it is checked. Server-side only, and
    /// valid only for the duration of <c>SaveFileAsync</c> — the entry is discarded afterwards.
    /// <para>
    /// Present so a hook whose permanent storage is the same service as the quarantine can promote
    /// the file with a server-side copy instead of streaming it back through the application. An
    /// S3-backed hook issues <c>CopyObject</c> on this handle and never touches the stream; one
    /// that doesn't recognise it simply reads <c>fileStream</c> as usual.
    /// </para>
    /// <para>
    /// Never serialised: it describes internal storage layout and has no meaning in the browser.
    /// Null when the context streams directly to storage, since there is no quarantine copy.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? QuarantineHandle { get; set; }

    // --- Cancellation/UI ---
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
            if (Status is TransferStatus.Completed or TransferStatus.Skipped) return 100;
            if (Status == TransferStatus.Pending) return 0;

            // Everything else - in flight, cancelled or failed - reports the progress of the
            // stage it is in, or died in. Uploading tracks the byte transfer; the stages after
            // it keep the bar full while the server works, so moving into the server phase does
            // not visibly reset the bar to zero.
            return Stage == TransferStage.Pending ? 0 : StageProgressPercent;
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