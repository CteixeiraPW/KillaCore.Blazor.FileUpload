namespace KillaCore.Blazor.FileUpload.Models;

public sealed class FileProcessingOptions
{
    // --- Endpoints ---
    public string UploadEndpointUrl { get; set; } = "api/upload";

    // ✅ SAFE: The user can ONLY add valid features here.
    // They cannot add "Uploading" or "Hashing" because the enum doesn't have them.
    public HashSet<FileUploadFeature> EnabledFeatures { get; set; } =
    [
        FileUploadFeature.VerifyLocalDuplicates,
        FileUploadFeature.VerifyRemoteDuplicates,
        FileUploadFeature.SaveToServer
    ];

    // --- Constraints ---
    public int MaxFiles { get; set; } = 10;
    public long MaxSizeFileBytes { get; set; } = 1024 * 1024 * 50; // 50MB
    public List<string> AllowedMimeTypes { get; set; } =
    [
        "image/jpeg", "image/png", "image/bmp", "image/tiff", "image/heif",
        "application/pdf", "text/html",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    // --- Concurrency & UI ---
    public int MaxConcurrentUploads { get; set; } = 5;      // Network Bound (JS Interop)
    public int MaxConcurrentProcessors { get; set; } = 2;   // CPU Bound (Hashing/Saving)
    public int UIProgressUpdateIntervalMs { get; set; } = 400;
}


public record TransferProgressWeights(
    double UploadWeight = 0.60, // 60% of total bar
    double HashWeight = 0.30,   // 30% of total bar
    double SaveWeight = 0.10    // 10% of total bar
)
{
    // Helper to ensure math is valid
    public bool IsValid => Math.Abs((UploadWeight + HashWeight + SaveWeight) - 1.0) < 0.001;
}