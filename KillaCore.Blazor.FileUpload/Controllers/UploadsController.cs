using FileSignatures;
using KillaCore.Blazor.FileUpload.Services; // Namespace for your Security Service
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KillaCore.Blazor.FileUpload.Controllers;

[ApiController]
[Route("api/uploads")]
public sealed class UploadsController(
    IWebHostEnvironment env,
    IFileUploadSecurityService securityService, // 1. Inject Security
    IFileUploadBridgeService bridge,         // 2. Inject the Bridge
    IFileFormatInspector inspector,    // 3. Inject the Magic Number Inspector
    IDataProtectionProvider dpProvider // 4. Data Protection Provider
    ) : ControllerBase
{
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

        using (var streamCheck = file.OpenReadStream())
        {
            // A. Identify the file (What IS it?)
            var detectedFormat = inspector.DetermineFileFormat(streamCheck);

            if (detectedFormat == null)
            {
                return BadRequest("File type not recognized.");
            }

            // B. POLICY CHECK (MimeType Based)
            // If allowedMimeTypes is NULL, we skip this check (Wildcard mode)
            if (allowedMimeTypes != null)
            {
                // FileSignatures provides the MediaType (e.g. "image/jpeg")
                bool isAllowed = allowedMimeTypes.Contains(detectedFormat.MediaType.ToLowerInvariant());

                if (!isAllowed)
                {
                    return BadRequest($"File type '{detectedFormat.MediaType}' is not allowed by policy.");
                }
            }

            // C. SPOOF CHECK: Does the Name match the Content?
            var userExt = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
            if (!IsExtensionValid(userExt, detectedFormat.Extension))
            {
                return BadRequest("Security Alert: File extension does not match file content.");
            }
        }


        // --- STEP 4: SAVE TO DISK (Standard Logic) ---
        // (Same as previous code...)
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