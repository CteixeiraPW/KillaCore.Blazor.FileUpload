namespace KillaCore.Blazor.FileUpload.Client.Models;

public sealed class FileProcessingOptions
{
    // --- Endpoints ---

    private string? _serverBaseUrl;

    /// <summary>
    /// Base URL of the upload server. Leave null/empty when client and server are the same app.
    /// Must include the scheme (e.g., "https://api.example.com" or "https://my.company/apps/myapp").
    /// </summary>
    public string? ServerBaseUrl
    {
        get => _serverBaseUrl;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"ServerBaseUrl must include the scheme (http:// or https://). Got: '{value}'",
                    nameof(ServerBaseUrl));
            }
            _serverBaseUrl = value?.TrimEnd('/');
        }
    }

    /// <summary>
    /// The resolved upload endpoint. Relative when same-origin, absolute when cross-origin.
    /// </summary>
    public string UploadEndpointUrl => string.IsNullOrEmpty(_serverBaseUrl)
        ? $"{FileUploadConstants.API_ROUTE_PREFIX}/temp"
        : $"{_serverBaseUrl}/{FileUploadConstants.API_ROUTE_PREFIX}/temp";


    // Identifies which server hook should handle this upload. Defaults to "Default".
    public string UploadContext { get; set; } = "Default";

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


    /// <summary>
    /// Returns a fingerprint of the policy-relevant fields.
    /// Used internally to detect when a policy token refresh is needed.
    /// </summary>
    internal string GetPolicyFingerprint()
    {
        // Only include fields that affect the server-side policy token
        var mimes = AllowedMimeTypes.Count == 0 ? "*" : string.Join(",", AllowedMimeTypes.Order());
        return $"{_serverBaseUrl}|{mimes}";
    }
}