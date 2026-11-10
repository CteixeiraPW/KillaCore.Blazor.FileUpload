namespace KillaCore.Blazor.FileUpload.Client.Models;

public static class FileUploadConstants
{
    public const string DATA_PROTECTION_POLICY = "KillaCore.Blazor.FileUpload.Policy.v1";
    public const string POLICY_HEADER_NAME = "X-Upload-Policy";
    public const string TOKEN_HEADER_NAME = "X-Upload-Token";
    public const string BATCH_ID_HEADER_NAME = "X-Batch-Id";
    public const string CONTEXT_HEADER_NAME = "X-Upload-Context";
    public const string API_ROUTE_PREFIX = "kc-api/uploads";

    /// <summary>
    /// Every request header a cross-origin deployment must allow in its CORS policy, ready to
    /// pass straight to <c>WithHeaders(...)</c>. Without them the browser's preflight fails
    /// before a single byte is uploaded.
    /// <para>
    /// <c>Content-Type</c> is included because the policy, token and batch-completion calls send
    /// <c>application/json</c>, which is not a CORS-safelisted content type. (The file transfer
    /// itself sends <c>multipart/form-data</c>, which is safelisted, but its custom headers are
    /// not.)
    /// </para>
    /// </summary>
    public static readonly string[] CORS_REQUEST_HEADERS =
    [
        POLICY_HEADER_NAME,
        TOKEN_HEADER_NAME,
        BATCH_ID_HEADER_NAME,
        CONTEXT_HEADER_NAME,
        "X-Requested-With",
        "Content-Type"
    ];
}
