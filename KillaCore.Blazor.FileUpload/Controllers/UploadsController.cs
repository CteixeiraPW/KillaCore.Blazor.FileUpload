using FileSignatures;
using HeyRed.Mime;
using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Filters;
using KillaCore.Blazor.FileUpload.Models;
using KillaCore.Blazor.FileUpload.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KillaCore.Blazor.FileUpload.Controllers;

[ApiController]
[Route(FileUploadConstants.API_ROUTE_PREFIX)]
public sealed class UploadsController(
    IFileUploadSecurityService securityService, // 1. Inject Security
    IBatchDuplicateTracker duplicateTracker, // 2. Inject the Batch Duplicate Tracker
    IFileFormatInspector inspector,    // 3. Inject the Magic Number Inspector
    IDataProtectionProvider dpProvider, // 4. Data Protection Provider
    IServiceProvider serviceProvider,   // 5. Inject Service Provider for Optional Hooks
    IOptions<FileUploadServerOptions> options,
    ILogger<UploadsController> logger
    ) : ControllerBase
{
    [HttpPost("policy")]
    public IActionResult GeneratePolicy([FromBody] List<string>? allowedMimeTypes)
    {
        var protector = dpProvider.CreateProtector(FileUploadConstants.DATA_PROTECTION_POLICY)
            .ToTimeLimitedDataProtector();
        string rulesPayload = allowedMimeTypes == null || allowedMimeTypes.Count == 0 ? "*" : string.Join(",", allowedMimeTypes);
        return Ok(new { token = protector.Protect(rulesPayload, TimeSpan.FromMinutes(30)) });
    }

    [HttpGet("token/{fileId}")]
    [EnforceAuthenticatedUser]
    public IActionResult GenerateToken(string fileId, [FromQuery] string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("UserId is required.");

        var token = securityService.GenerateToken(fileId, userId);
        return Ok(new { token });
    }

    [HttpPost("batch/{batchId}/complete")]
    public async Task<IActionResult> CompleteBatch(string batchId, [FromBody] List<FileTransferData> files)
    {
        // Security: Verify this batch actually had files uploaded through our pipeline
        if (!duplicateTracker.TryCompleteBatch(batchId))
        {
            logger.LogWarning("Rejected batch completion for unknown or already-completed batch: {BatchId}", batchId);
            return BadRequest("Batch not found or already completed.");
        }

        var hooks = serviceProvider.GetService<IFileUploadServerHooks>();

        if (hooks != null)
        {
            // Execute the developer's server-side logic (e.g., updating database records, 
            // sending an email summary, or triggering an AI processor)
            await hooks.OnBatchCompletedAsync(batchId, files);
        }

        return Ok();
    }

    // List of extensions that do not have "Magic Numbers" and must be validated by content
    private static readonly HashSet<string> _textExtensions = [".txt", ".md", ".markdown", ".json", ".csv", ".xml", ".html", ".css", ".js"];

    [AllowAnonymous]
    [HttpPost("temp")]
    [RequestSizeLimit(1024 * 1024 * 500)]
    public async Task<IActionResult> UploadTemp([FromForm] IFormFile file, CancellationToken ct)
    {
        // --- STEP 1: EXTRACT & VALIDATE POLICY (The "What is Allowed?" check) ---

        if (!Request.Headers.TryGetValue(FileUploadConstants.POLICY_HEADER_NAME, out var policyHeader))
        {
            return BadRequest("Missing upload policy.");
        }

        HashSet<string>? allowedMimeTypes = null;
        try
        {
            var protector = dpProvider.CreateProtector(FileUploadConstants.DATA_PROTECTION_POLICY)
                .ToTimeLimitedDataProtector();
            string decryptedRules = protector.Unprotect(policyHeader.ToString());

            if (decryptedRules != "*")
            {
                allowedMimeTypes = [.. decryptedRules
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant())];
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest("Invalid security policy.");
        }

        // --- STEP 2: SECURITY & REPLAY CHECK ---

        if (!Request.Headers.TryGetValue(FileUploadConstants.TOKEN_HEADER_NAME, out var tokenValues))
            return Unauthorized("Missing upload token.");

        var secureToken = tokenValues.ToString();

        if (!securityService.ValidateToken(secureToken, out string? fileId, out string? tokenUserId))
            return Unauthorized("Invalid or expired token.");

        if (!duplicateTracker.RegisterUsedToken(secureToken))
            return BadRequest("This upload token has already been used.");

        // --- STEP 3: CONTENT INSPECTION (The "Is it Real?" check) ---

        if (file == null || file.Length == 0) return BadRequest("Empty file.");

        var userExtension = Path.GetExtension(file.FileName).ToLowerInvariant();

        using (var streamCheck = file.OpenReadStream())
        {
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
        string folderConfig = options.Value.TempFolder;
        if (string.IsNullOrWhiteSpace(folderConfig)) folderConfig = "KillaCoreUploads";

        var tempRoot = Path.IsPathRooted(folderConfig)
            ? folderConfig
            : Path.Combine(Path.GetTempPath(), folderConfig);

        Directory.CreateDirectory(tempRoot);
        var tempId = $"{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Combine(tempRoot, tempId);

        try
        {
            await using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await file.CopyToAsync(fs, ct);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return StatusCode(499); // Client closed request
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            if(logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "Error saving uploaded file to disk.");
            return Problem("An internal error occurred while saving the file.");

        }

        // --- STEP 5: HANDOVER & EXECUTE HOOKS ---
        var handoffToken = Guid.NewGuid().ToString("N");
        string uploadContext = Request.Headers["X-Upload-Context"].FirstOrDefault() ?? "Default";
        Dictionary<string, string?> metadata = [];

        IFileUploadServerHooks? hooks = serviceProvider.GetKeyedService<IFileUploadServerHooks>(uploadContext);
        hooks ??= serviceProvider.GetService<IFileUploadServerHooks>();

        string? finalId = null;

        if (hooks != null)
        {
            string clientBatchId = Request.Headers.TryGetValue("X-Batch-Id", out var bId)
                ? bId.ToString()
                : string.Empty;

            var model = new FileTransferData
            {
                Id = handoffToken,
                FileName = file.FileName,
                FileSize = file.Length,
                MimeType = file.ContentType ?? "application/octet-stream",
                BatchId = clientBatchId
            };

            try
            {
                using (var hashStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    var hashBytes = await sha256.ComputeHashAsync(hashStream, ct);
                    model.DetectedHash = Convert.ToHexString(hashBytes);
                }

                bool isDuplicate = false;

                if (model.DetectedHash != null)
                {
                    // UPDATED: Using the new tracker!
                    if (!duplicateTracker.TryRegisterBatchHash(clientBatchId, model.DetectedHash))
                    {
                        TryDelete(tempPath);
                        return BadRequest("Identical file content already uploaded in this batch.");
                    }

                    isDuplicate = await hooks.CheckRemoteDuplicateAsync(model.DetectedHash, ct);
                    metadata = model.Metadata;
                }

                if (!isDuplicate)
                {
                    await using var finalStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                    await hooks.SaveFileAsync(model, finalStream, ct);
                    finalId = model.FinalResourceId;
                    metadata = model.Metadata;
                }
                else
                {
                    return BadRequest("File is a remote duplicate.");
                }
            }
            catch (Exception ex)
            {
                if(logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Error processing uploaded file for context '{Context}'", uploadContext);
                return BadRequest($"Server processing error.");
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        else
        {
            // --- CLEANED UP: FAIL FAST MECHANISM ---
            TryDelete(tempPath);
            return StatusCode(500, $"Configuration Error: No IFileUploadServerHooks implementation was registered for context '{uploadContext}'. Please register it in Program.cs.");
        }

        return Ok(new { token = handoffToken, size = file.Length, finalId, metadata});
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