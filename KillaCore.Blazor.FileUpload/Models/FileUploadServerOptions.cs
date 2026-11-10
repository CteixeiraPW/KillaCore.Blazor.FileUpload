namespace KillaCore.Blazor.FileUpload.Models;

/// <summary>
/// Overrides the root limits for one upload context. Both members are nullable so a profile can
/// change the size without restating the type list, or vice versa.
/// </summary>
public class UploadProfile
{
    /// <summary>Largest accepted file, in bytes. Null inherits the root value.</summary>
    public long? MaxFileBytes { get; set; }

    /// <summary>Accepted MIME types. Null inherits the root list; empty means any type.</summary>
    public List<string>? AllowedMimeTypes { get; set; }

    /// <summary>Stream straight to final storage for this context. Null inherits the root value.</summary>
    public bool? StreamDirectToStorage { get; set; }
}

/// <summary>The limits that actually apply to a given upload context.</summary>
/// <param name="MaxFileBytes">Largest accepted file, in bytes.</param>
/// <param name="AllowedMimeTypes">Accepted MIME types; an empty list means any type.</param>
/// <param name="StreamDirectToStorage">
/// Whether the hook receives the request body itself rather than a completed temp file.
/// </param>
public sealed record EffectiveUploadLimits(
    long MaxFileBytes,
    IReadOnlyList<string> AllowedMimeTypes,
    bool StreamDirectToStorage);

public class FileUploadServerOptions
{
    public const string DefaultConfigSectionName = "KillaCoreFileUpload";

    /// <summary>
    /// The secret key used to sign HMAC Anti-Replay tokens. Must be at least 16 characters.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The folder to store temporary files during the upload process.
    /// If an absolute path is provided (e.g., "D:\TempUploads"), it will be used directly.
    /// If a relative name is provided, it will be placed inside the OS's default Temp directory.
    /// </summary>
    public string TempFolder { get; set; } = "KillaCoreUploads";

    /// <summary>
    /// Largest accepted file, in bytes. This is the authoritative limit: the client's
    /// <c>MaxSizeFileBytes</c> only decides how quickly a user is told, never whether the server
    /// accepts the file.
    /// </summary>
    public long MaxFileBytes { get; set; } = 1024L * 1024L * 50L;

    /// <summary>
    /// Used when nothing is configured anywhere. Held separately from the property because the
    /// configuration binder ADDS to a pre-populated list rather than replacing it — a default
    /// list in the initializer would mean configuring a restrictive policy silently produced the
    /// union of yours and ours, so configuring would widen the policy instead of narrowing it.
    /// </summary>
    private static readonly string[] _builtInAllowedMimeTypes =
    [
        "image/jpeg", "image/png", "image/bmp", "image/tiff", "image/heif",
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    /// <summary>
    /// Accepted MIME types, checked against the file's actual content on the server.
    /// <para>
    /// Three distinct states: <c>null</c> (not configured — the built-in list applies), an empty
    /// list (explicitly any type), or a list of types.
    /// </para>
    /// <para>
    /// <c>text/html</c> is deliberately absent from the built-in list: HTML has no magic number,
    /// so it can only be validated as text, and HTML served back from the application's own origin
    /// executes as a page. See the README note on serving uploaded content before adding it.
    /// </para>
    /// </summary>
    public List<string>? AllowedMimeTypes { get; set; }

    /// <summary>
    /// Hard transport ceiling applied to Kestrel and to multipart form parsing, independent of
    /// any profile. This is what actually aborts an oversized request mid-stream; the per-profile
    /// <see cref="MaxFileBytes"/> is a policy check on a request the server agreed to read.
    /// Set it at or above the largest profile limit.
    /// </summary>
    public long MaxRequestBytes { get; set; } = 1024L * 1024L * 500L;

    /// <summary>
    /// Hand <c>SaveFileAsync</c> the request body itself instead of writing a temp file first.
    /// <para>
    /// The fast path: one write, to your storage, with the hash computed as the hook reads. The
    /// buffered default costs an extra write and read, and buys three things this gives up —
    /// a seekable stream with a known <c>Length</c>, a hash and file size known <i>before</i>
    /// saving, and the chance to decline a file without having written it.
    /// </para>
    /// <para>
    /// Duplicate detection is therefore skipped entirely for a context in this mode: both checks
    /// exist to decide something before the save, and there is no longer a before. For the same
    /// reason it cannot be combined with scanning the file prior to storage — there is no
    /// quarantine copy to scan.
    /// </para>
    /// </summary>
    public bool StreamDirectToStorage { get; set; }

    /// <summary>
    /// Per-context overrides, keyed by the client's <c>UploadContext</c> — the same key that
    /// selects the <c>IFileUploadServerHooks</c> implementation. Most applications leave this
    /// empty and configure the root values only.
    /// </summary>
    public Dictionary<string, UploadProfile> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the limits in force for an upload context: the named profile's values where it
    /// sets them, the root values otherwise. The single answer to "what applies here", used both
    /// to enforce on upload and to report through the limits endpoint, so the two cannot drift.
    /// </summary>
    public EffectiveUploadLimits GetEffectiveLimits(string? uploadContext)
    {
        UploadProfile? profile = null;

        if (!string.IsNullOrWhiteSpace(uploadContext))
            Profiles.TryGetValue(uploadContext, out profile);

        // Each ?? step falls back only when that level said nothing at all. An empty list is a
        // statement — "any type" — and is therefore honoured rather than skipped.
        IReadOnlyList<string> allowedTypes =
            profile?.AllowedMimeTypes
            ?? AllowedMimeTypes
            ?? (IReadOnlyList<string>)_builtInAllowedMimeTypes;

        return new EffectiveUploadLimits(
            profile?.MaxFileBytes ?? MaxFileBytes,
            allowedTypes,
            profile?.StreamDirectToStorage ?? StreamDirectToStorage);
    }
}
