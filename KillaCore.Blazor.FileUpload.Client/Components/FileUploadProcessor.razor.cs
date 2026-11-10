using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;


namespace KillaCore.Blazor.FileUpload.Client.Components;

public partial class FileUploadProcessor : ComponentBase, IAsyncDisposable
{
    // --- 1. INJECTIONS ---
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    [Inject] private IFileUploadClientService ClientService { get; set; } = default!;

    [Inject] private ILogger<FileUploadProcessor> Logger { get; set; } = default!;

    // --- 2. CONFIGURATION ---
    [Parameter, EditorRequired]
    public FileProcessingOptions Options { get; set; } = new();

    [Parameter]
    public string UserId { get; set; } = "Anonymous";

    [Parameter, EditorRequired]
    public string InputSelector { get; set; } = default!;

    /// <summary>
    /// Supplies extra headers for the file transfer itself — typically <c>Authorization</c>.
    /// <para>
    /// The package's own <c>X-Upload-Token</c> proves the request came through this pipeline; it
    /// says nothing about <i>who</i> the user is. Bearer-token applications need this hook,
    /// because an XHR carries no <c>Authorization</c> header of its own.
    /// </para>
    /// <para>
    /// Called ONCE per batch, before the first file starts. A batch of large files can run for
    /// minutes, so a short-lived token may expire before the last file is sent; fetching per file
    /// would instead cost one token request per upload. If your tokens are short relative to your
    /// batches, lower <see cref="FileProcessingOptions.MaxFiles"/> rather than relying on a
    /// mid-batch refresh. If this throws, the batch fails before anything is sent.
    /// </para>
    /// <para>
    /// The package's own header names are reserved and are ignored if returned here.
    /// </para>
    /// </summary>
    [Parameter] public Func<Task<IReadOnlyDictionary<string, string>>>? GetUploadHeadersAsync { get; set; }

    /// <summary>
    /// Sends cookies and HTTP authentication with the file transfer (<c>xhr.withCredentials</c>).
    /// <para>
    /// Only affects cross-origin uploads — a same-origin request always carries cookies. When set,
    /// the API's CORS policy must use <c>AllowCredentials()</c> together with an explicit
    /// <c>WithOrigins(...)</c>; the browser rejects credentials against <c>AllowAnyOrigin()</c>.
    /// </para>
    /// </summary>
    [Parameter] public bool SendCredentials { get; set; }

    // --- 3. EVENT HOOKS ---
    [Parameter] public EventCallback<FileNotificationEvent> OnEvent { get; set; }

    [Parameter] public Func<IReadOnlyList<FileTransferData>, string, Task>? OnFilesUploadCompleted { get; set; }

    // --- 4. PUBLIC STATE ---
    private readonly List<FileTransferData> _transfers = [];
    public IReadOnlyList<FileTransferData> Transfers => _transfers;

    // --- INTERNAL STATE ---
    private IJSObjectReference? _jsModule;
    private CancellationTokenSource? _batchCts;
    private readonly CancellationTokenSource _disposalCts = new();

    private DotNetObjectReference<FileUploadProcessor>? _dotNetRef;
    private string _currentBatchId = string.Empty;
    private string? _previousPolicyFingerprint;

    // State for the Policy Token
    private string _policyToken = string.Empty;

    private record UploadResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("token")] string Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("finalId")] string? FinalId,
        [property: System.Text.Json.Serialization.JsonPropertyName("metadata")] Dictionary<string, string?> Metadata,
        [property: System.Text.Json.Serialization.JsonPropertyName("outcome")] string? Outcome
    );

    // --- LIFECYCLE ---
    protected override void OnInitialized()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    protected override async Task OnParametersSetAsync()
    {
        var currentFingerprint = Options.GetPolicyFingerprint();
        if (currentFingerprint == _previousPolicyFingerprint) return;
        _previousPolicyFingerprint = currentFingerprint;

        try
        {
            _policyToken = await ClientService.GetPolicyTokenAsync(Options, _disposalCts.Token);
        }
        catch (OperationCanceledException) { /* Component disposed, safe to ignore */ }
        catch (Exception ex)
        {
            await FireEventAsync(EventNotificationType.BatchFailed, null, $"Failed to get upload policy from server: {ex.Message}");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _jsModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/KillaCore.Blazor.FileUpload.Client/js/fileUploadWorker.js");
            }
            catch (Exception ex)
            {
                await FireEventAsync(EventNotificationType.BatchFailed, null, $"Failed to load JS: {ex.Message}");
            }
        }
    }

    // --- PUBLIC ACTIONS ---
    /// <summary>
    /// Cancels any batch still in flight and resets the transfer list.
    /// </summary>
    public void Clear()
    {
        // Signal the in-flight producer loop FIRST, so it stops dispatching new files before
        // anything it depends on is taken away underneath it.
        if (_batchCts is { IsCancellationRequested: false })
            _batchCts.Cancel();

        // The superseded source is deliberately not disposed here: the loop it drives may still
        // be unwinding and holding tokens linked to it. It owns no timer and no unmanaged state,
        // so it is reclaimed normally once that loop lets go of it.
        _batchCts = new CancellationTokenSource();

        // Disposing a model's IndividualCts while the loop is still linking against it throws
        // ObjectDisposedException from inside Parallel.ForEachAsync, which then surfaces as a
        // spurious BatchFailed on the NEXT batch. Only reclaim models that are provably done;
        // the rest are plain managed objects and are collected once this list drops them.
        foreach (var transfer in _transfers)
        {
            if (transfer.IsFinished)
                transfer.Dispose();
        }

        _transfers.Clear();
        _currentBatchId = Guid.NewGuid().ToString();
    }

    public async Task ProcessInputFiles(IReadOnlyList<IBrowserFile> browserFiles)
    {
        Clear();

        // Two counters, deliberately separate:
        //  - domIndex is the file's position in the DOM <input> FileList. The JS worker reads
        //    input.files[Index], so it MUST advance for every file, accepted or skipped.
        //  - acceptedCount is the MaxFiles budget, and advances only for files we will upload.
        int domIndex = 0;
        int acceptedCount = 0;

        foreach (var file in browserFiles)
        {
            string effectiveContentType = GetEffectiveContentType(file);

            var model = new FileTransferData(file.Name, file.Size, effectiveContentType)
            {
                Index = domIndex++,
                BatchId = _currentBatchId,
                Status = TransferStatus.Pending,
            };

            _transfers.Add(model);

            if (file.Size > Options.MaxSizeFileBytes)
            {
                model.Status = TransferStatus.Skipped;
                model.StartTime = DateTime.UtcNow;
                model.EndTime = DateTime.UtcNow;
                model.StatusMessage = "File too large";
                await FireEventAsync(EventNotificationType.FileSkipped, model, "Size limit exceeded");
                continue;
            }

            if (Options.AllowedMimeTypes.Count != 0 && !Options.AllowedMimeTypes.Contains(effectiveContentType))
            {
                model.Status = TransferStatus.Skipped;
                model.StartTime = DateTime.UtcNow;
                model.EndTime = DateTime.UtcNow;
                model.StatusMessage = "Invalid type";
                await FireEventAsync(EventNotificationType.FileSkipped, model, $"MimeType '{effectiveContentType}' not allowed");
                continue;
            }

            if (Options.VerifyLocalDuplicates)
            {
                // Look at the transfers we ALREADY accepted in this specific batch
                bool isLocalDuplicate = _transfers.Any(t =>
                    t.Id != model.Id && // Don't compare against itself
                    t.Status == TransferStatus.Pending && // Only check against files we are actually going to upload
                    t.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase) &&
                    t.FileSize == file.Size);

                if (isLocalDuplicate)
                {
                    model.Status = TransferStatus.Skipped;
                    model.StartTime = DateTime.UtcNow;
                    model.EndTime = DateTime.UtcNow;
                    model.StatusMessage = "Duplicate in batch";
                    await FireEventAsync(EventNotificationType.FileSkipped, model, "Local duplicate detected");
                    continue;
                }
            }

            if (acceptedCount >= Options.MaxFiles)
            {
                model.Status = TransferStatus.Skipped;
                model.StartTime = DateTime.UtcNow;
                model.EndTime = DateTime.UtcNow;
                model.StatusMessage = "Limit exceeded";
                await FireEventAsync(EventNotificationType.FileSkipped, model, "Max files reached");
                continue;
            }

            // Survived every check, so this file consumes one slot of the MaxFiles budget.
            acceptedCount++;
        }

        if (_transfers.All(t => t.IsFinished)) return;

        await FireEventAsync(EventNotificationType.BatchStarted);

        // Fire-and-forget: starts the upload pipeline without blocking the UI.
        // Note: In Blazor WASM (single-threaded), this does NOT offload to a background thread.
        // Concurrency is achieved via async yielding at await points (JS interop, HTTP calls).
        _ = RunNetworkProducerAsync(_batchCts!.Token).ContinueWith(async task =>
        {
            if (task.Exception?.InnerException is { } ex)
            {
                await FireEventAsync(EventNotificationType.BatchFailed, null, ex.Message);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task CancelAllAsync()
    {
        if (_batchCts != null && !_batchCts.IsCancellationRequested)
            _batchCts?.Cancel();

        foreach (var t in _transfers.Where(x => !x.IsFinished))
        {
            t.Status = TransferStatus.Cancelled;
            t.StatusMessage = "Batch Cancelled";
        }

        await FireEventAsync(EventNotificationType.BatchCancelled);
    }

    public async Task CancelFile(string fileId)
    {
        var model = _transfers.FirstOrDefault(x => x.Id == fileId);
        if (model != null && !model.IsFinished)
        {
            try
            {
                if (!model.IndividualCts.IsCancellationRequested)
                    model.IndividualCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already reclaimed by a concurrent Clear()/dispose. There is no transfer left to
                // abort, but the status change below still belongs on the model.
            }

            model.Status = TransferStatus.Cancelled;
            model.EndTime = DateTime.UtcNow;
            model.StatusMessage = "Cancelled";
            await FireEventAsync(EventNotificationType.FileCancelled, model);
        }
    }

    [JSInvokable]
    public void ReportUploadProgress(string batchId, int fileIndex, double percent)
    {
        if (batchId != _currentBatchId) return;

        var model = _transfers.FirstOrDefault(x => x.Index == fileIndex);

        if (model is null || model.IsFinished) return;

        model.StageProgressPercent = percent;

        // Once every byte is sent the transfer is no longer uploading: the request stays open
        // while the server inspects, hashes, checks for duplicates and runs SaveFileAsync.
        // Without this the row reads "Uploading 100%" for that entire phase and looks hung.
        //
        // The fields are set directly rather than through UpdateStage, which zeroes
        // StageProgressPercent and would make the bar drop from full back to empty here.
        if (percent >= 100 && model.Stage == TransferStage.Uploading)
        {
            model.Stage = TransferStage.ServerSaving;
            model.StatusMessage = "Processing on server...";
            _ = FireEventAsync(EventNotificationType.StageChange, model);
            return;
        }

        _ = FireEventAsync(EventNotificationType.Progress, model);
    }

    /// <summary>
    /// Uploads all pending files concurrently (up to MaxConcurrentUploads).
    /// 
    /// WASM NOTE: Blazor WebAssembly is single-threaded. Parallel.ForEachAsync does NOT achieve
    /// true CPU parallelism here. However, it still provides concurrent async I/O because each
    /// iteration awaits JS interop (XMLHttpRequest), which yields the thread and allows other
    /// iterations to proceed. The MaxDegreeOfParallelism effectively controls how many concurrent
    /// XHR requests are in-flight simultaneously.
    /// </summary>
    private async Task RunNetworkProducerAsync(CancellationToken batchToken)
    {
        var pendingModels = _transfers.Where(x => x.Status == TransferStatus.Pending).ToList();

        // Fetched once for the whole batch. Without them a bearer-token API rejects every file,
        // so a failure here fails the batch rather than producing N identical 401s.
        IReadOnlyDictionary<string, string>? uploadHeaders = null;

        if (GetUploadHeadersAsync is not null)
        {
            try
            {
                uploadHeaders = await GetUploadHeadersAsync();
            }
            catch (Exception ex)
            {
                foreach (var model in pendingModels)
                    FailModel(model, $"Could not obtain upload headers: {ex.Message}");

                await FireEventAsync(EventNotificationType.BatchFailed, null, ex.Message);
                return;
            }
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Options.MaxConcurrentUploads,
            CancellationToken = batchToken
        };

        try
        {
            await Parallel.ForEachAsync(pendingModels, parallelOptions, async (model, token) =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, model.IndividualCts.Token);
                var ct = linkedCts.Token;

                // Captured once so a Clear() that starts a new batch mid-flight cannot retarget
                // either the upload or its abort.
                var batchIdAtStart = _currentBatchId;

                // Blazor's interop token only abandons the .NET await, so without this the
                // browser keeps streaming the whole file after the user has cancelled. Covers
                // every cancellation path at once: batch cancel, single-file cancel, disposal.
                using var abortOnCancel = ct.Register(() => AbortTransfer(batchIdAtStart, model.Index));

                try
                {
                    UpdateStage(model, TransferStage.Uploading, "Uploading...");

                    var uploadToken = await ClientService.GetUploadTokenAsync(Options, model.Id, UserId, ct);

                    if (string.IsNullOrEmpty(uploadToken))
                        throw new Exception("Server rejected upload token request.");

                    model.UploadToken = uploadToken;
                    model.StartTime = DateTime.UtcNow;

                    if (_jsModule != null)
                    {
                        var response = await _jsModule.InvokeAsync<UploadResponse>(
                            "uploadFile",
                            ct,
                            _dotNetRef,
                            InputSelector,
                            model.Index,
                            Options.UploadEndpointUrl,
                            batchIdAtStart,
                            uploadToken,
                            FileUploadConstants.TOKEN_HEADER_NAME,
                            _policyToken,
                            FileUploadConstants.POLICY_HEADER_NAME,
                            Options.UploadContext,
                            new { headers = uploadHeaders, withCredentials = SendCredentials }
                            );

                        if (response != null && !string.IsNullOrEmpty(response.Token))
                        {
                            model.FinalResourceId = response.FinalId;
                            model.Metadata = response.Metadata;
                            model.Stage = TransferStage.Completed;
                            model.EndTime = DateTime.UtcNow;

                            // Parsed from a string rather than deserialised as an enum: the host
                            // application owns the server's JSON options, so the wire stays text.
                            // An unrecognised value falls back to Saved, matching older servers.
                            if (!Enum.TryParse(response.Outcome, ignoreCase: true, out UploadOutcome outcome))
                                outcome = UploadOutcome.Saved;

                            if (outcome == UploadOutcome.Saved)
                            {
                                model.Status = TransferStatus.Completed;
                                model.StatusMessage = "Upload complete";
                                await FireEventAsync(EventNotificationType.FileCompleted, model);
                            }
                            else
                            {
                                // The server deliberately declined to store a second copy. That is
                                // a skip, not a failure — FinalResourceId may point at the copy
                                // that already exists.
                                model.Status = TransferStatus.Skipped;
                                model.StatusMessage = outcome == UploadOutcome.DuplicateInBatch
                                    ? "Duplicate in batch"
                                    : "Already on server";
                                await FireEventAsync(EventNotificationType.FileSkipped, model, model.StatusMessage);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    model.Status = TransferStatus.Cancelled;
                    model.EndTime = DateTime.UtcNow;
                    model.StatusMessage = "Cancelled";
                }
                catch (Exception ex)
                {
                    FailModel(model, $"Upload error: {ex.Message}");
                }
            });
        }
        catch (OperationCanceledException) { /* Batch Cancelled */
            await FireEventAsync(EventNotificationType.BatchCancelled);
        }
        finally
        {
            if (!batchToken.IsCancellationRequested)
            {
                await FireEventAsync(EventNotificationType.BatchCompleted);

                // 1. Tell the Server API that the batch is done
                try
                {
                    // Every transfer in the batch is reported, not just the successful ones: each
                    // carries its own Status, so hooks can log or notify on failures and skips —
                    // including a batch in which every single file failed.
                    var batchTransfers = _transfers.ToList();

                    await ClientService.NotifyBatchCompletedAsync(Options, _currentBatchId, batchTransfers, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    if (Logger.IsEnabled(LogLevel.Error))
                        Logger.LogError("[API Error] Failed to notify batch completed:{Message}", ex.Message);
                }

                // 2. Fire the UI's local callback
                if (OnFilesUploadCompleted != null)
                {
                    try 
                    {
                        await OnFilesUploadCompleted(_transfers, _currentBatchId); 
                    } catch (Exception ex)
                    {
                        if(Logger.IsEnabled(LogLevel.Error))
                            Logger.LogError("[Callback Error] OnFilesUploadCompleted threw an exception:{Message}", ex.Message);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Fire-and-forget abort of a single browser transfer, invoked from a cancellation callback.
    /// </summary>
    private void AbortTransfer(string batchId, int fileIndex)
    {
        var module = _jsModule;
        if (module is null) return;

        _ = AbortTransferAsync(module, batchId, fileIndex);
    }

    private async Task AbortTransferAsync(IJSObjectReference module, string batchId, int fileIndex)
    {
        try
        {
            // Deliberately no CancellationToken: the token that triggered this abort is already
            // cancelled, so passing it would cancel the abort call itself.
            await module.InvokeVoidAsync("cancelUpload", batchId, fileIndex);
        }
        catch (JSDisconnectedException) { /* Blazor Server circuit is gone; nothing to abort */ }
        catch (ObjectDisposedException) { /* module disposed during teardown */ }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("Could not abort transfer {Index}: {Message}", fileIndex, ex.Message);
        }
    }

    private void UpdateStage(FileTransferData model, TransferStage stage, string msg)
    {
        model.Stage = stage;
        model.StatusMessage = msg;
        model.StageProgressPercent = 0;

        // Move the model out of Pending the moment real work begins. LifecyclePercent returns 0
        // for anything still Pending, so without this the reported StageProgressPercent is
        // discarded and the progress bar sits at 0% for the whole transfer, then snaps to 100%.
        if (!model.IsFinished && stage is not (TransferStage.Pending or TransferStage.Completed))
        {
            model.Status = TransferStatus.InProgress;
        }

        _ = FireEventAsync(EventNotificationType.StageChange, model);
    }

    private void FailModel(FileTransferData model, string error)
    {
        model.Status = TransferStatus.Failed;
        model.StatusMessage = error;
        model.EndTime = DateTime.UtcNow;
        _ = FireEventAsync(EventNotificationType.FileFailed, model);
    }

    private async Task FireEventAsync(EventNotificationType type, FileTransferData? model = null, string? msg = null)
    {
        if (!OnEvent.HasDelegate) return;

        try
        {
            await InvokeAsync(() => OnEvent.InvokeAsync(new FileNotificationEvent(type, model, msg)));
        }
        catch (Exception) { /* Safe to ignore disconnected circuits */ }
    }

    public async ValueTask DisposeAsync()
    {
        _disposalCts.Cancel();
        _disposalCts.Dispose();

        if (_batchCts != null && !_batchCts.IsCancellationRequested)
            _batchCts?.Cancel();

        // Stop any in-flight browser transfers deterministically before the JS module goes away.
        // The per-file cancellation callbacks above are fire-and-forget and would race this
        // teardown, so the batch sweep is awaited here instead.
        if (_jsModule is not null && !string.IsNullOrEmpty(_currentBatchId))
        {
            try
            {
                await _jsModule.InvokeVoidAsync("cancelBatch", _currentBatchId);
            }
            catch (JSDisconnectedException) { /* circuit already gone */ }
            catch (ObjectDisposedException) { /* module already disposed */ }
            catch (Exception) { /* the page may be unloading; nothing useful to do */ }
        }

        foreach (var transfer in _transfers)
        {
            transfer.Dispose();
        }
        _transfers.Clear();
        _dotNetRef?.Dispose();
        if (_jsModule != null)
        {
            try { await _jsModule.DisposeAsync(); } catch { }
        }
        _batchCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string GetEffectiveContentType(IBrowserFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType) && file.ContentType != "application/octet-stream")
        {
            return file.ContentType;
        }

        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        return extension switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}