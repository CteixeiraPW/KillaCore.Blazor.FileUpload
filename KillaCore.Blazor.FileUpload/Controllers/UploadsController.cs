using FileSignatures;
using HeyRed.Mime;
using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Filters;
using KillaCore.Blazor.FileUpload.Models;
using KillaCore.Blazor.FileUpload.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
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
    IUploadQuarantineStore quarantine, // 4. Holds the file while it is checked
    IServiceProvider serviceProvider,   // 5. Inject Service Provider for Optional Hooks
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
        if (!duplicateTracker.TryCompleteBatch(batchId, out var completion) || completion is null)
        {
            logger.LogWarning("Rejected batch completion for unknown or already-completed batch: {BatchId}", batchId);
            return BadRequest("Batch not found or already completed.");
        }

        var hooks = ResolveHooks(completion.UploadContext);

        if (hooks != null)
        {
            // Execute the developer's server-side logic (e.g., updating database records,
            // sending an email summary, or triggering an AI processor)
            await hooks.OnBatchCompletedAsync(batchId, MergeBatchRecords(files, completion.ServerRecords));
        }

        return Ok();
    }

    /// <summary>
    /// The client posts the batch's file list, but only the server knows what became of the files
    /// that actually arrived — the SHA-256 above all, which is computed server-side and never sent
    /// back. Echoing the client's own copy would mean trusting a value the server is supposed to
    /// be establishing, so server records win wherever they exist.
    /// <para>
    /// Client entries survive only for files it skipped locally and never sent, which the server
    /// has no record of at all.
    /// </para>
    /// </summary>
    private static IReadOnlyList<FileTransferData> MergeBatchRecords(
        IReadOnlyList<FileTransferData> clientFiles,
        IReadOnlyList<FileTransferData> serverRecords)
    {
        if (serverRecords.Count == 0) return clientFiles;

        var byId = serverRecords.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var merged = new List<FileTransferData>(clientFiles.Count);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var clientFile in clientFiles)
        {
            if (byId.TryGetValue(clientFile.Id, out var serverFile))
            {
                merged.Add(serverFile);
                matched.Add(serverFile.Id);
            }
            else
            {
                merged.Add(clientFile);
            }
        }

        // A file the server processed but the client never reported — a batch cancelled in the
        // browser mid-flight, say — still belongs in the hook's view of what happened.
        foreach (var serverFile in serverRecords)
        {
            if (!matched.Contains(serverFile.Id))
                merged.Add(serverFile);
        }

        return merged;
    }

    // List of extensions that do not have "Magic Numbers" and must be validated by content
    private static readonly HashSet<string> _textExtensions = [".txt", ".md", ".markdown", ".json", ".csv", ".xml", ".html", ".css", ".js"];

    private const string DEFAULT_UPLOAD_CONTEXT = "Default";

    /// <summary>
    /// How much of the file is held in memory for the signature and text checks. Magic numbers
    /// live in the first few bytes and the text scan only ever looked at 4KB, so a forged file is
    /// rejected after this much rather than after its full length.
    /// </summary>
    private const int INSPECTION_HEAD_BYTES = 8 * 1024;

    /// <summary>
    /// Resolves the developer's hooks for a given upload context, falling back to a
    /// non-keyed registration. Shared by the upload and batch-completion actions so the
    /// two cannot drift apart.
    /// </summary>
    private IFileUploadServerHooks? ResolveHooks(string uploadContext)
        => serviceProvider.GetKeyedService<IFileUploadServerHooks>(uploadContext)
           ?? serviceProvider.GetService<IFileUploadServerHooks>();

    /// <summary>
    /// Resolves the optional scanner for a context, by the same keyed-then-fallback rule as the
    /// hooks. Null means this context is not scanned.
    /// </summary>
    private IUploadScanner? ResolveScanner(string uploadContext)
        => serviceProvider.GetKeyedService<IUploadScanner>(uploadContext)
           ?? serviceProvider.GetService<IUploadScanner>();

    // Deliberately carries NO [AllowAnonymous]: hard-coding it would override the host
    // application's authorization fallback policy, leaving this endpoint open in an app that had
    // locked everything else down, with no way for the consumer to close it. Apps that want
    // anonymous uploads ask for them via AddKillaCoreFileUploadServer(allowAnonymousUploads: true).
    [HttpPost("temp")]
    [ServiceFilter(typeof(RequestSizeGuardFilter))]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> UploadTemp(CancellationToken ct)
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

        // --- STEP 3: FIND THE FILE PART ---

        // Read the body ourselves rather than binding an IFormFile. Model binding would buffer
        // every byte to disk before this method ran, so the whole file was written once before
        // anything could look at it — including a file whose signature was about to be rejected.
        var boundary = MultipartRequestHelper.GetBoundary(Request.ContentType);
        if (boundary is null)
            return BadRequest("Expected a multipart/form-data request with a valid boundary.");

        var reader = new MultipartReader(boundary, Request.Body);

        MultipartSection? section;
        string fileName = string.Empty;

        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            if (MultipartRequestHelper.IsFileSection(section, out fileName))
                break;
        }

        if (section is null)
            return BadRequest("No file part found in the request.");

        var contentType = section.ContentType ?? "application/octet-stream";
        var userExtension = Path.GetExtension(fileName).ToLowerInvariant();

        // --- STEP 4: INSPECT THE HEAD, BEFORE A SINGLE BYTE IS STORED ---

        // Signatures live in the first few bytes, and the text check only ever looked at the
        // first 4KB. Reading just the head means a forged file costs 8KB instead of its full size.
        var head = new byte[INSPECTION_HEAD_BYTES];
        int headLength = await section.Body.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);

        if (headLength == 0)
            return BadRequest("Empty file.");

        using (var headStream = new MemoryStream(head, 0, headLength, writable: false))
        {
            var (IsValid, Error) = _textExtensions.Contains(userExtension)
                ? ValidateTextFile(headStream, userExtension, allowedMimeTypes)
                : ValidateBinarySignature(headStream, fileName, allowedMimeTypes);

            if (!IsValid) return BadRequest(Error);
        }

        // --- STEP 5: HANDOVER & EXECUTE HOOKS ---

        IFileUploadServerHooks? hooks = ResolveHooks(uploadContext);

        if (hooks is null)
        {
            return StatusCode(500, $"Configuration Error: No IFileUploadServerHooks implementation was registered for context '{uploadContext}'. Please register it in Program.cs.");
        }

        var handoffToken = Guid.NewGuid().ToString("N");

        var model = new FileTransferData
        {
            // The client's own id, carried in the signed token. Using it rather than a fresh GUID
            // lets the batch-completion hook join the server's records to the client's list.
            Id = string.IsNullOrEmpty(fileId) ? handoffToken : fileId,
            FileName = fileName,
            MimeType = contentType,
            BatchId = clientBatchId
        };

        var uploadOutcome = UploadOutcome.Saved;
        string? outcomeDetail = null;

        try
        {
            if (limits.StreamDirectToStorage)
            {
                // One write, straight to the hook's storage. The hash and the size are by-products
                // of the hook's own copy, so neither exists until it has drained the stream — and
                // with nothing buffered there is nothing to check a duplicate against beforehand.
                var body = new HashingReadStream(head.AsMemory(0, headLength), section.Body, limits.MaxFileBytes);

                try
                {
                    await hooks.SaveFileAsync(model, body, ct);
                    model.FileSize = body.TotalBytesRead;
                    model.DetectedHash = body.ComputedHash;
                }
                finally
                {
                    body.Dispose();
                }
            }
            else
            {
                var result = await ProcessBufferedAsync(
                    section!, head, headLength, model, limits, hooks,
                    ResolveScanner(uploadContext), clientBatchId, ct);
                if (result.Rejection is not null) return result.Rejection;

                uploadOutcome = result.Outcome;
                outcomeDetail = result.Detail;
            }
        }
        catch (UploadTooLargeException ex)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499); // Client closed request
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "Error processing uploaded file for context '{Context}'", uploadContext);
            return BadRequest("Server processing error.");
        }

        // The server's own record of this file, so OnBatchCompletedAsync reports server-computed
        // values such as the hash rather than whatever the client chose to send back.
        // A duplicate is a benign skip; an infected file is a failure. The distinction matters to
        // anyone reading the batch records afterwards.
        model.Status = uploadOutcome switch
        {
            UploadOutcome.Saved => TransferStatus.Completed,
            UploadOutcome.Infected => TransferStatus.Failed,
            _ => TransferStatus.Skipped
        };

        model.StatusMessage = outcomeDetail ?? model.StatusMessage;

        duplicateTracker.RecordFile(clientBatchId, model);

        // outcome travels as a string so the response does not depend on the host application's
        // JSON enum settings, which this library does not control.
        return Ok(new
        {
            token = handoffToken,
            size = model.FileSize,
            finalId = model.FinalResourceId,
            metadata = model.Metadata,
            outcome = uploadOutcome.ToString(),
            outcomeDetail
        });
    }

    /// <summary>Outcome of the buffered path, plus anything worth telling the user about it.</summary>
    private sealed record BufferedResult(
        UploadOutcome Outcome,
        IActionResult? Rejection = null,
        string? Detail = null);

    /// <summary>
    /// The buffered path: stream the body into <c>TempFolder</c>, hashing as it goes, then run the
    /// duplicate checks and hand the completed file to the hook.
    /// <para>
    /// The temp file is what makes "decide before writing" possible — a duplicate is declined
    /// without ever reaching storage — and it is the natural place to scan a file before it
    /// becomes reachable.
    /// </para>
    /// </summary>
    private async Task<BufferedResult> ProcessBufferedAsync(
        MultipartSection section,
        byte[] head,
        int headLength,
        FileTransferData model,
        EffectiveUploadLimits limits,
        IFileUploadServerHooks hooks,
        IUploadScanner? scanner,
        string clientBatchId,
        CancellationToken ct)
    {
        string? handle = null;

        try
        {
            // The same wrapper the direct path uses. The store consumes the body, and the size and
            // SHA-256 fall out of how much it read — one pass, wherever the quarantine lives.
            var body = new HashingReadStream(head.AsMemory(0, headLength), section.Body, limits.MaxFileBytes);

            try
            {
                handle = await quarantine.WriteAsync(model, body, ct);
                model.FileSize = body.TotalBytesRead;
                model.DetectedHash = body.ComputedHash;
            }
            finally
            {
                body.Dispose();
            }

            // Offered to the hook so storage that owns both ends can promote the file with a
            // server-side copy instead of streaming it back through this process.
            model.QuarantineHandle = handle;

            // A duplicate is NOT an error: the pipeline worked and correctly declined to store a
            // second copy. Both cases are reported as a successful request carrying a non-Saved
            // outcome, which the client surfaces as Skipped rather than Failed.
            if (!duplicateTracker.TryRegisterBatchHash(clientBatchId, model.DetectedHash))
                return new BufferedResult(UploadOutcome.DuplicateInBatch);

            if (await hooks.CheckRemoteDuplicateAsync(model, ct))
                return new BufferedResult(UploadOutcome.DuplicateOnServer);

            // Scanned here and nowhere else: the only moment the file exists in full and is still
            // unreachable. After the duplicate checks, so nothing is scanned that was never going
            // to be stored. A scanner that throws rejects the upload rather than waving it on.
            if (scanner is not null)
            {
                await using var toScan = await quarantine.OpenReadAsync(handle, ct);
                var verdict = await scanner.ScanAsync(model, toScan, ct);

                if (!verdict.IsClean)
                {
                    var threat = string.IsNullOrWhiteSpace(verdict.Threat) ? "unspecified" : verdict.Threat;

                    if (logger.IsEnabled(LogLevel.Warning))
                        logger.LogWarning("Rejected infected upload {FileName} ({Threat}).", model.FileName, threat);

                    return new BufferedResult(UploadOutcome.Infected, Detail: $"Rejected by virus scan: {threat}");
                }
            }

            await using (var content = await quarantine.OpenReadAsync(handle, ct))
            {
                await hooks.SaveFileAsync(model, content, ct);
            }

            return new BufferedResult(UploadOutcome.Saved);
        }
        finally
        {
            // The handle names an entry that is about to stop existing, so it must not survive
            // into the batch records the completion hook sees.
            model.QuarantineHandle = null;

            if (handle is not null)
            {
                try
                {
                    // Deliberately not the request token: a cancelled upload still has to clean up.
                    await quarantine.DeleteAsync(handle, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                        logger.LogWarning(ex, "Could not discard quarantined upload {Handle}.", handle);
                }
            }
        }
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


    private static bool IsExtensionValid(string userExt, string detectedExt)
    {
        if (userExt == detectedExt) return true;
        if (detectedExt == "jpeg" && userExt == "jpg") return true;
        if (detectedExt == "tiff" && userExt == "tif") return true;
        if (detectedExt == "mpeg" && userExt == "mpg") return true;
        return false;
    }
}