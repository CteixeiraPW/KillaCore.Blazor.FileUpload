using FileSignatures;
using HeyRed.Mime;
using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Filters;
using KillaCore.Blazor.FileUpload.Models;
using KillaCore.Blazor.FileUpload.Services;
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
    IServiceProvider serviceProvider,   // 4. Inject Service Provider for Optional Hooks
    IOptions<FileUploadServerOptions> options,
    ILogger<UploadsController> logger
    ) : ControllerBase
{
    /// <summary>
    /// Reports the limits in force for an upload context, so the component can pre-filter against
    /// the server's real rules instead of the developer restating them client-side.
    /// <para>
    /// Read-only and unsigned, which is the point: this replaced an endpoint that signed whatever
    /// rules the caller asked for. Nothing here is secret — the limits are observable from the
    /// pipeline's behaviour anyway — and the server enforces them regardless of what the client
    /// does with the answer.
    /// </para>
    /// </summary>
    [HttpGet("policy/{uploadContext}")]
    public IActionResult GetLimits(string uploadContext)
    {
        var limits = options.Value.GetEffectiveLimits(uploadContext);

        return Ok(new
        {
            maxFileBytes = limits.MaxFileBytes,
            allowedMimeTypes = limits.AllowedMimeTypes
        });
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
        // Security: Verify this batch actually had files uploaded through our pipeline.
        // The upload context comes from the server-side batch record, not from the caller,
        // so a completion request cannot route itself to a different set of hooks.
        if (!duplicateTracker.TryCompleteBatch(batchId, out var batchUploadContext))
        {
            logger.LogWarning("Rejected batch completion for unknown or already-completed batch: {BatchId}", batchId);
            return BadRequest("Batch not found or already completed.");
        }

        var hooks = ResolveHooks(batchUploadContext ?? DEFAULT_UPLOAD_CONTEXT);

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

    private const string DEFAULT_UPLOAD_CONTEXT = "Default";

    /// <summary>
    /// Resolves the developer's hooks for a given upload context, falling back to a
    /// non-keyed registration. Shared by the upload and batch-completion actions so the
    /// two cannot drift apart.
    /// </summary>
    private IFileUploadServerHooks? ResolveHooks(string uploadContext)
        => serviceProvider.GetKeyedService<IFileUploadServerHooks>(uploadContext)
           ?? serviceProvider.GetService<IFileUploadServerHooks>();

    // Deliberately carries NO [AllowAnonymous]: hard-coding it would override the host
    // application's authorization fallback policy, leaving this endpoint open in an app that had
    // locked everything else down, with no way for the consumer to close it. Apps that want
    // anonymous uploads ask for them via AddKillaCoreFileUploadServer(allowAnonymousUploads: true).
    [HttpPost("temp")]
    [ServiceFilter(typeof(RequestSizeGuardFilter))]
    public async Task<IActionResult> UploadTemp([FromForm] IFormFile file, CancellationToken ct)
    {
        // --- STEP 1: SECURITY & REPLAY CHECK ---

        if (!Request.Headers.TryGetValue(FileUploadConstants.TOKEN_HEADER_NAME, out var tokenValues))
            return Unauthorized("Missing upload token.");

        var secureToken = tokenValues.ToString();

        if (!securityService.ValidateToken(secureToken, out string? fileId, out string? tokenUserId))
            return Unauthorized("Invalid or expired token.");

        if (!duplicateTracker.RegisterUsedToken(secureToken))
            return BadRequest("This upload token has already been used.");

        // Record the batch as legitimate now that a valid, unused token has been presented —
        // before content validation, so that a batch whose files ALL fail validation can still
        // report completion and let the app log or notify on it.
        string uploadContext = Request.Headers[FileUploadConstants.CONTEXT_HEADER_NAME].FirstOrDefault()
            ?? DEFAULT_UPLOAD_CONTEXT;
        string clientBatchId = Request.Headers.TryGetValue(FileUploadConstants.BATCH_ID_HEADER_NAME, out var bId)
            ? bId.ToString()
            : string.Empty;

        duplicateTracker.RegisterBatch(clientBatchId, uploadContext);

        // --- STEP 2: RESOLVE THE LIMITS IN FORCE (the "what is allowed?" check) ---

        // Read from server configuration keyed by the upload context, never from the request.
        // The caller may choose among profiles the developer defined; it cannot describe one.
        var limits = options.Value.GetEffectiveLimits(uploadContext);

        HashSet<string>? allowedMimeTypes = limits.AllowedMimeTypes.Count == 0
            ? null // an empty list means any type
            : new HashSet<string>(limits.AllowedMimeTypes, StringComparer.OrdinalIgnoreCase);

        // --- STEP 3: CONTENT INSPECTION (The "Is it Real?" check) ---

        if (file == null || file.Length == 0) return BadRequest("Empty file.");

        // The authoritative size check. RequestSizeGuardFilter already turned away anything whose
        // declared Content-Length was over the limit, but that header is advisory — absent on
        // chunked requests — so the real length is checked here as well.
        if (file.Length > limits.MaxFileBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"File exceeds the {limits.MaxFileBytes:N0} byte limit for this upload.");
        }

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
        Dictionary<string, string?> metadata = [];

        IFileUploadServerHooks? hooks = ResolveHooks(uploadContext);

        string? finalId = null;
        var uploadOutcome = UploadOutcome.Saved;

        if (hooks != null)
        {
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

                // A duplicate is NOT an error: the pipeline worked and correctly declined to store
                // a second copy. Both cases are reported as a successful request carrying a
                // non-Saved outcome, which the client surfaces as Skipped rather than Failed.
                if (model.DetectedHash != null)
                {
                    if (!duplicateTracker.TryRegisterBatchHash(clientBatchId, model.DetectedHash))
                    {
                        uploadOutcome = UploadOutcome.DuplicateInBatch;
                    }
                    else if (await hooks.CheckRemoteDuplicateAsync(model, ct))
                    {
                        // The hook may have described the existing copy on the model.
                        uploadOutcome = UploadOutcome.DuplicateOnServer;
                    }
                }

                if (uploadOutcome == UploadOutcome.Saved)
                {
                    await using var finalStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                    await hooks.SaveFileAsync(model, finalStream, ct);
                }

                finalId = model.FinalResourceId;
                metadata = model.Metadata;
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

        // outcome travels as a string so the response does not depend on the host application's
        // JSON enum settings, which this library does not control.
        return Ok(new { token = handoffToken, size = file.Length, finalId, metadata, outcome = uploadOutcome.ToString() });
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