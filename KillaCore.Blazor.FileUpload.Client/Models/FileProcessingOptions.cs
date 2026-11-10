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
    /// Resolves one of the package's API routes against <see cref="ServerBaseUrl"/>.
    /// <para>
    /// Relative when same-origin, so the caller's <c>HttpClient.BaseAddress</c> (or the page's
    /// own origin, for the XHR worker) applies and apps hosted under a sub-path keep working.
    /// Absolute once a server base URL is set, so EVERY call in the pipeline goes to the same
    /// host: the upload token and the policy are minted and verified by the same server, which
    /// is the only way the signatures and Data Protection keys can line up.
    /// </para>
    /// </summary>
    /// <param name="relativePath">Route beneath the API prefix, e.g. <c>"policy"</c>.</param>
    public string ResolveApiUrl(string relativePath) => string.IsNullOrEmpty(_serverBaseUrl)
        ? $"{FileUploadConstants.API_ROUTE_PREFIX}/{relativePath}"
        : $"{_serverBaseUrl}/{FileUploadConstants.API_ROUTE_PREFIX}/{relativePath}";

    /// <summary>
    /// The resolved upload endpoint. Relative when same-origin, absolute when cross-origin.
    /// </summary>
    public string UploadEndpointUrl => ResolveApiUrl("temp");


    // Identifies which server hook should handle this upload. Defaults to "Default".
    public string UploadContext { get; set; } = "Default";

    /// <summary>
    /// Skip a file when an earlier file in the same selection already has the same name and
    /// size. This is a client-side convenience only: the server independently rejects content
    /// whose SHA-256 already appeared in the batch, whatever this is set to.
    /// </summary>
    public bool VerifyLocalDuplicates { get; set; } = true;

    // --- Constraints ---
    public int MaxFiles { get; set; } = 10;
    public long MaxSizeFileBytes { get; set; } = 1024 * 1024 * 50; // 50MB
    /// <summary>
    /// MIME types the client will accept, also sent to the server as the upload policy.
    /// An empty list means any type.
    /// <para>
    /// <c>text/html</c> is deliberately NOT in this default. HTML carries no magic number, so it
    /// can only be validated as "text", and an HTML file served back from the application's own
    /// origin executes as a page — stored XSS. Add it only if you have decided how uploaded
    /// content is served; see the README note on serving uploaded content.
    /// </para>
    /// </summary>
    public List<string> AllowedMimeTypes { get; set; } =
    [
        "image/jpeg", "image/png", "image/bmp", "image/tiff", "image/heif",
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    // --- Concurrency ---

    /// <summary>
    /// How many XHR transfers may be in flight at once.
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 5;


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