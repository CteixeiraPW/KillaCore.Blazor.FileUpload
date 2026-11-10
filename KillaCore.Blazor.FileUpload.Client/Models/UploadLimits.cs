namespace KillaCore.Blazor.FileUpload.Client.Models;

/// <summary>
/// The limits the server reports for an upload context.
/// <para>
/// Fetched rather than declared, so the limits are configured once — server-side, where they are
/// also enforced — and the UI follows automatically instead of restating them. The client-side
/// values on <see cref="FileProcessingOptions"/> remain only as a fallback for the window before
/// this arrives, or if the fetch fails.
/// </para>
/// </summary>
/// <param name="MaxFileBytes">Largest accepted file, in bytes.</param>
/// <param name="AllowedMimeTypes">Accepted MIME types; an empty list means any type.</param>
public sealed record UploadLimits(long MaxFileBytes, IReadOnlyList<string> AllowedMimeTypes);
