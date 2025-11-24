namespace KillaCore.Blazor.FileUpload.Models;

public sealed class FileTransferData(string fileName, long fileSize, string mimeType, TransferProgressWeights weights) : IDisposable
{
    // --- Standard Properties ---
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int Index { get; set; }
    public string BatchId { get; set; } = string.Empty;
    public string FileName { get; } = fileName;
    public long FileSize { get; } = fileSize;
    public string MimeType { get; } = mimeType;

    // --- State ---
    public TransferStage Stage { get; set; } = TransferStage.Pending;
    public TransferStatus Status { get; set; } = TransferStatus.Pending;
    public string StatusMessage { get; set; } = "Queued";

    // "Local" Progress (0-100) for the current active stage
    public double StageProgressPercent { get; set; }

    // --- Artifacts ---
    public string? UploadToken { get; set; }
    public string? DetectedHash { get; set; }
    public string? FinalResourceId { get; set; }

    // --- Cancellation/UI ---
    public DateTime LastUIUpdate { get; set; } = DateTime.MinValue;
    public DateTime StartTime { get; set; } // [NEW] Good for calculating speed
    public DateTime? EndTime { get; set; }  // [NEW] Good for analytics
    public CancellationTokenSource IndividualCts { get; set; } = new();

    public bool IsFinished => Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled or TransferStatus.Skipped;

    // --- The Logic ---
    public double LifecyclePercent
    {
        get
        {
            if (Status == TransferStatus.Completed) return 100;
            if (Stage == TransferStage.Pending) return 0;

            // Base accumulator
            double total = 0;

            // 1. UPLOADING (Always happens)
            // If we are currently uploading, we add portion of the UploadWeight.
            // If we passed uploading, we add the FULL UploadWeight.
            if (Stage == TransferStage.Uploading)
            {
                return weights.UploadWeight * StageProgressPercent;
            }
            total += weights.UploadWeight * 100;

            // 2. HASHING (Optional)
            if (weights.HashWeight > 0)
            {
                if (Stage == TransferStage.Hashing)
                {
                    return total + (weights.HashWeight * StageProgressPercent);
                }
                // If we are past hashing (Verifying or Saving), add full Hash weight
                // Note: We check if stage > Hashing to avoid adding it prematurely
                if (Stage > TransferStage.Hashing)
                {
                    total += weights.HashWeight * 100;
                }
            }

            // 3. SAVING (Optional)
            if (weights.SaveWeight > 0)
            {
                if (Stage == TransferStage.ServerSaving)
                {
                    return total + (weights.SaveWeight * StageProgressPercent);
                }
                // If completed, loop top catches it. If skipped, it won't be here.
            }

            // 4. "Fixed" Stages (Verification)
            // These stages happen instantly or have no progress bar. 
            // The user sees the bar "full" up to the previous step while these run.
            // We just return the total accumulated so far.
            return Math.Min(total, 100);
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

internal static class FileTransferDataHelpers
{
    internal static TransferProgressWeights CalculateWeights(FileProcessingOptions options)
    {
        // 1. Check the SAFE enum to see what is enabled
        bool isHashingActive = options.EnabledFeatures.Contains(FileUploadFeature.VerifyLocalDuplicates)
                            || options.EnabledFeatures.Contains(FileUploadFeature.VerifyRemoteDuplicates);

        bool isSavingActive = options.EnabledFeatures.Contains(FileUploadFeature.SaveToServer);

        // 2. Assign "Effort Points" (Relative difficulty)
        double uploadPoints = 6.0;
        double hashPoints = isHashingActive ? 3.0 : 0;
        double savePoints = isSavingActive ? 1.0 : 0;

        double totalPoints = uploadPoints + hashPoints + savePoints;

        // 3. Normalize
        return new TransferProgressWeights(
            UploadWeight: uploadPoints / totalPoints,
            HashWeight: hashPoints / totalPoints,
            SaveWeight: savePoints / totalPoints
        );
    }
}
