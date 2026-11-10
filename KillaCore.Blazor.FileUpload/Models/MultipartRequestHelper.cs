using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace KillaCore.Blazor.FileUpload.Models;

/// <summary>
/// Small helpers for reading a multipart body by hand instead of through model binding.
/// </summary>
internal static class MultipartRequestHelper
{
    /// <summary>Boundaries longer than this are refused rather than trusted.</summary>
    private const int BoundaryLengthLimit = 128;

    public static bool IsMultipartContentType(string? contentType) =>
        !string.IsNullOrEmpty(contentType) &&
        contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the boundary, refusing a missing or absurdly long one. An unbounded boundary is a
    /// denial-of-service vector, which is why the framework asks callers to impose a limit.
    /// </summary>
    public static string? GetBoundary(string? contentType)
    {
        if (!IsMultipartContentType(contentType))
            return null;

        var boundary = HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(contentType).Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > BoundaryLengthLimit)
            return null;

        return boundary;
    }

    /// <summary>
    /// True for a section carrying file content: a content-disposition with a filename, as
    /// opposed to a plain form field.
    /// </summary>
    public static bool IsFileSection(MultipartSection section, out string fileName)
    {
        fileName = string.Empty;

        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            return false;

        if (!disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName;
        if (!raw.HasValue)
            return false;

        // The client controls this string; keep only the leaf so a crafted name cannot escape a
        // directory. Callers still generate their own storage names.
        fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(raw).Value ?? string.Empty);

        return !string.IsNullOrWhiteSpace(fileName);
    }
}
