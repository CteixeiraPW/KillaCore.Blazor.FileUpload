using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Collections.Concurrent;


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
        [property: System.Text.Json.Serialization.JsonPropertyName("metadata")] Dictionary<string, string?> Metadata
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
    public void Clear()
    {
        if (_batchCts != null && !_batchCts.IsCancellationRequested)
            _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();

        foreach (var transfer in _transfers)
        {
            transfer.Dispose();
        }

        _transfers.Clear();
        _currentBatchId = Guid.NewGuid().ToString();
    }

    public async Task ProcessInputFiles(IReadOnlyList<IBrowserFile> browserFiles)
    {
        Clear();

        int acceptedCount = 0;

        foreach (var file in browserFiles)
        {
            string effectiveContentType = GetEffectiveContentType(file);

            var model = new FileTransferData(file.Name, file.Size, effectiveContentType)
            {
                Index = acceptedCount,
                BatchId = _currentBatchId,
                Status = TransferStatus.Pending,
            };
            acceptedCount++;

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

            if (Options.EnabledFeatures.Contains(FileUploadFeature.VerifyLocalDuplicates))
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

            if (acceptedCount > Options.MaxFiles)
            {
                model.Status = TransferStatus.Skipped;
                model.StartTime = DateTime.UtcNow;
                model.EndTime = DateTime.UtcNow;
                model.StatusMessage = "Limit exceeded";
                await FireEventAsync(EventNotificationType.FileSkipped, model, "Max files reached");
                continue;
            }
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
            if (model.IndividualCts != null && !model.IndividualCts.IsCancellationRequested)
                model.IndividualCts.Cancel();
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

        if (model != null && !model.IsFinished)
        {
            model.StageProgressPercent = percent;
            _ = FireEventAsync(EventNotificationType.Progress, model);
        }
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

                try
                {
                    UpdateStage(model, TransferStage.Uploading, "Uploading...");

                    var uploadToken = await ClientService.GetUploadTokenAsync(model.Id, UserId, ct);

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
                            _currentBatchId,
                            uploadToken,
                            FileUploadConstants.TOKEN_HEADER_NAME,
                            _policyToken,
                            FileUploadConstants.POLICY_HEADER_NAME,
                            Options.UploadContext
                            );

                        if (response != null && !string.IsNullOrEmpty(response.Token))
                        {
                            model.FinalResourceId = response.FinalId;
                            model.Metadata = response.Metadata;
                            model.Stage = TransferStage.Completed; // Assuming you have this enum
                            model.Status = TransferStatus.Completed; // CRITICAL FIX
                            model.EndTime = DateTime.UtcNow;
                            model.StatusMessage = "Upload complete";
                            await FireEventAsync(EventNotificationType.FileCompleted, model);
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
        catch (OperationCanceledException) { /* Batch Cancelled */ }
        finally
        {
            await FireEventAsync(EventNotificationType.BatchCompleted);

            if (!batchToken.IsCancellationRequested)
            {
                // 1. Tell the Server API that the batch is done
                try
                {
                    //only completed files are relevant to the server, failed/skipped/cancelled files are not sent in the notification
                    var completedTransfers = _transfers.Where(t => t.Status == TransferStatus.Completed).ToList();

                    await ClientService.NotifyBatchCompletedAsync(_currentBatchId, completedTransfers, CancellationToken.None);
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

    private void UpdateStage(FileTransferData model, TransferStage stage, string msg)
    {
        model.Stage = stage;
        model.StatusMessage = msg;
        model.StageProgressPercent = 0;
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