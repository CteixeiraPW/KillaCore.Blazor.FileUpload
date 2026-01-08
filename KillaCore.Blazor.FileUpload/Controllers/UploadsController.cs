using FileSignatures;
using HeyRed.Mime;
using KillaCore.Blazor.FileUpload.Services; // Namespace for your Security Service
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;


namespace KillaCore.Blazor.FileUpload.Controllers;

[ApiController]
[Route("api/uploads")]
public sealed class UploadsController(
    IFileUploadSecurityService securityService, // 1. Inject Security
    IFileUploadBridgeService bridge,         // 2. Inject the Bridge
    IFileFormatInspector inspector,    // 3. Inject the Magic Number Inspector
    IDataProtectionProvider dpProvider // 4. Data Protection Provider
    ) : ControllerBase
{

    // List of extensions that do not have "Magic Numbers" and must be validated by content
    private static readonly HashSet<string> _textExtensions = [".txt", ".md", ".markdown", ".json", ".csv", ".xml", ".html", ".css", ".js"];

    [AllowAnonymous] // We allow anonymous because we rely on the Token
    [HttpPost("temp")]
    [RequestSizeLimit(1024 * 1024 * 500)]
    public async Task<IActionResult> UploadTemp([FromForm] IFormFile file, CancellationToken ct)
    {
        // --- STEP 1: EXTRACT & VALIDATE POLICY (The "What is Allowed?" check) ---

        if (!Request.Headers.TryGetValue(FileUpload.Components.FileUploadProcessor.POLICY_HEADER_NAME, out var policyHeader))
        {
            return BadRequest("Missing upload policy.");
        }

        HashSet<string>? allowedMimeTypes = null;
        try
        {
            // A. Create the Protector using the EXACT same purpose string as your Blazor Component
            var protector = dpProvider.CreateProtector(FileUpload.Components.FileUploadProcessor.DATA_PROTECTION_POLICY);

            // B. Decrypt the rules (e.g., "jpg,png,pdf")
            string decryptedRules = protector.Unprotect(policyHeader.ToString());

            // C. Create a fast lookup set (Normalize to lowercase, remove dots)
            // Handle Wildcard "*"
            if (decryptedRules != "*")
            {
                allowedMimeTypes = [.. decryptedRules
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().ToLowerInvariant())];
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // This means the token was tampered with or created by a different server key
            return BadRequest("Invalid security policy.");
        }

        // --- STEP 2: SECURITY & REPLAY CHECK ---

        if (!Request.Headers.TryGetValue(FileUpload.Components.FileUploadProcessor.TOKEN_HEADER_NAME, out var tokenValues))
            return Unauthorized("Missing upload token.");

        var secureToken = tokenValues.ToString();
        var userId = User.Identity?.Name ?? "Anonymous";

        if (!securityService.ValidateToken(secureToken, userId, out _))
            return Unauthorized("Invalid or expired token.");

        if (!bridge.RegisterUsedKey(secureToken))
            return BadRequest("This upload token has already been used.");

        // --- STEP 3: CONTENT INSPECTION (The "Is it Real?" check) ---

        if (file == null || file.Length == 0) return BadRequest("Empty file.");

        var userExtension = Path.GetExtension(file.FileName).ToLowerInvariant();

        using (var streamCheck = file.OpenReadStream())
        {
            // [LOGIC SWITCH]
            // If it is a text file, we validate UTF8 content.
            // If it is binary, we validate Magic Numbers (Signature).
            if (_textExtensions.Contains(userExtension))
            {
                var (IsValid, Error) = ValidateTextFile(streamCheck, userExtension, allowedMimeTypes);
                if (!IsValid) return BadRequest(Error);
            }
            else
            {
                var (IsValid, Error) = ValidateBinarySignature(streamCheck, file.FileName, allowedMimeTypes);
                if (!IsValid) return BadRequest(Error);
            }
        }

        // --- STEP 4: SAVE TO DISK (Standard Logic) ---
        var tempRoot = Path.Combine(Path.GetTempPath(), "KillaCoreUploads");

        Directory.CreateDirectory(tempRoot);
        var tempId = $"{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Combine(tempRoot, tempId);

        try
        {
            await using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await file.CopyToAsync(fs, ct);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return Problem(ex.Message);
        }

        // --- STEP 5: HANDOVER ---
        var handoffToken = Guid.NewGuid().ToString("N");
        bridge.RegisterFile(handoffToken, tempPath);

        return Ok(new { token = handoffToken, size = file.Length });
    }

    private static (bool IsValid, string? Error) ValidateTextFile(Stream stream, string extension, HashSet<string>? allowedMimes)
    {
        // 1. Infer MimeType using the Library (Source of Truth)
        string assumedMime;
        try
        {
            // "extension" already comes in with a dot (e.g. ".md") from the calling method
            assumedMime = MimeTypesMap.GetMimeType(extension);
        }
        catch
        {
            // Fallback for unknown extensions
            assumedMime = "application/octet-stream";
        }

        // 2. Policy Check
        if (allowedMimes != null && !allowedMimes.Contains(assumedMime))
        {
            return (false, $"File type '{assumedMime}' is not allowed by policy.");
        }

        // 3. Content Check (UTF-8 Validation)
        if (!IsValidUtf8Text(stream))
        {
            return (false, "Security Alert: File contains binary data but claims to be text.");
        }

        return (true, null);
    }

    private (bool IsValid, string? Error) ValidateBinarySignature(Stream stream, string fileName, HashSet<string>? allowedMimes)
    {
        // 1. Identify the file (Magic Number Check)
        var detectedFormat = inspector.DetermineFileFormat(stream);

        if (detectedFormat == null)
        {
            // If inspector returns null, it's either an unknown binary or a text file that slipped through
            return (false, "File type not recognized or signature missing.");
        }

        // 2. Policy Check (MimeType Based)
        if (allowedMimes != null)
        {
            bool isAllowed = allowedMimes.Contains(detectedFormat.MediaType.ToLowerInvariant());
            if (!isAllowed)
            {
                return (false, $"File type '{detectedFormat.MediaType}' is not allowed by policy.");
            }
        }

        // 3. Spoof Check: Does the Name match the Content?
        var userExt = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!IsExtensionValid(userExt, detectedFormat.Extension))
        {
            return (false, "Security Alert: File extension does not match file content.");
        }

        return (true, null);
    }

    private static bool IsValidUtf8Text(Stream stream)
    {
        try
        {
            if (stream.Length == 0) return true;

            // Reset position just in case
            stream.Position = 0;

            int b;
            // Check first 4KB
            int checkLength = Math.Min((int)stream.Length, 4096);

            for (int i = 0; i < checkLength; i++)
            {
                b = stream.ReadByte();
                // A null byte (0x00) is the strongest indicator of a binary file.
                // Valid text (even Markdown) almost never contains null bytes.
                if (b == 0)
                {
                    stream.Position = 0;
                    return false;
                }
            }

            stream.Position = 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch { /* Swallow cleanup errors */ }
    }

    private static bool IsExtensionValid(string userExt, string detectedExt)
    {
        if (userExt == detectedExt) return true;
        if (detectedExt == "jpeg" && userExt == "jpg") return true;
        if (detectedExt == "tiff" && userExt == "tif") return true;
        if (detectedExt == "mpeg" && userExt == "mpg") return true;
        return false;
    }
}