using KillaCore.Blazor.FileUpload.Models;
using KillaCore.Blazor.FileUpload.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace KillaCore.Blazor.FileUpload.Components;

public partial class FileUploadProcessor : ComponentBase, IAsyncDisposable
{
    // --- 1. INJECTIONS ---
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private IFileUploadSecurityService SecurityService { get; set; } = default!;
    [Inject] private IFileUploadBridgeService Bridge { get; set; } = default!; // Injected Bridge
    [Inject] private IDataProtectionProvider DataProtectionProvider { get; set; } = default!; // Injected DP

    // --- 2. CONFIGURATION ---
    [Parameter, EditorRequired]
    public FileProcessingOptions Options { get; set; } = new();

    [Parameter]
    public string UserId { get; set; } = "Anonymous";

    [Parameter, EditorRequired]
    public string InputSelector { get; set; } = default!;

    // --- 3. EVENT HOOKS ---
    [Parameter] public EventCallback<FileNotificationEvent> OnEvent { get; set; }
    [Parameter] public Func<FileTransferData, Task<bool>>? OnVerifyRemoteDuplicate { get; set; }
    [Parameter] public Func<FileTransferData, Stream, Task>? OnFileUploadCompleted { get; set; }

    // --- 4. PUBLIC STATE ---
    private readonly List<FileTransferData> _transfers = [];
    public IReadOnlyList<FileTransferData> Transfers => _transfers;

    // --- INTERNAL STATE ---
    private IJSObjectReference? _jsModule;
    private CancellationTokenSource? _batchCts;
    private Channel<ProcessingJob>? _cpuChannel;
    private DotNetObjectReference<FileUploadProcessor>? _dotNetRef;
    private readonly ConcurrentDictionary<string, string> _localHashCache = new();
    private string _currentBatchId = string.Empty;

    // State for the Policy Token
    private string _policyToken = string.Empty;

    // CHANGED: Job now holds the ClaimToken, not the Path (Security)
    private record ProcessingJob(FileTransferData Model, string ClaimToken);

    internal const string DATA_PROTECTION_POLICY = "KillaCore.Blazor.FileUpload.Policy.v1";
    internal const string POLICY_HEADER_NAME = "X-Upload-Policy";
    internal const string TOKEN_HEADER_NAME = "X-Upload-Token";

    // --- LIFECYCLE ---
    protected override void OnInitialized()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    protected override void OnParametersSet()
    {
        // 1. Re-create the protector (lightweight operation)
        var protector = DataProtectionProvider.CreateProtector(DATA_PROTECTION_POLICY);

        // 2. Extract Rules from Options
        // Logic: If the list is empty, your client-side logic implies "Allow All".
        // We represent "Allow All" in the token as a wildcard "*".
        string rulesPayload;

        if (Options.AllowedMimeTypes == null || Options.AllowedMimeTypes.Count == 0)
        {
            rulesPayload = "*";
        }
        else
        {
            // Join into a simple comma-separated string: "image/jpeg,image/png"
            rulesPayload = string.Join(",", Options.AllowedMimeTypes);
        }

        // 3. Encrypt (This generates a new token whenever Options change)
        _policyToken = protector.Protect(rulesPayload);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                // Lazy load the JS Module
                _jsModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/KillaCore.Blazor.FileUpload/js/fileUploadWorker.js");
            }
            catch (Exception ex)
            {
                await FireEventAsync(EventNotificationType.BatchFailed, null, $"Failed to load JS: {ex.Message}");
            }
        }
    }

    // --- PUBLIC ACTIONS ---

    public async Task ProcessInputFiles(IReadOnlyList<IBrowserFile> browserFiles)
    {
        // 1. Reset State
        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        _transfers.Clear();
        _localHashCache.Clear();
        _currentBatchId = Guid.NewGuid().ToString();

        // 2. Calculate Weights based on Options (for accurate progress bars)
        var weights = FileTransferDataHelpers.CalculateWeights(Options);

        // 3. Filter & Enforce Limits (Stage 1 & 2)
        int acceptedCount = 0;

        foreach (var file in browserFiles)
        {
            // Create Model
            var model = new FileTransferData(file.Name, file.Size, file.ContentType, weights)
            {
                Index = acceptedCount, // Tracks index in the browserFileList
                BatchId = _currentBatchId,
                Status = TransferStatus.Pending
            };

            _transfers.Add(model);

            // A. Validate Size/Type
            if (file.Size > Options.MaxSizeFileBytes)
            {
                model.Status = TransferStatus.Skipped;
                model.StatusMessage = "File too large";
                await FireEventAsync(EventNotificationType.FileSkipped, model, "Size limit exceeded");
                continue;
            }

            if (Options.AllowedMimeTypes.Count != 0 && !Options.AllowedMimeTypes.Contains(file.ContentType))
            {
                model.Status = TransferStatus.Skipped;
                model.StatusMessage = "Invalid type";
                await FireEventAsync(EventNotificationType.FileSkipped, model, "MimeType not allowed");
                continue;
            }

            // B. Validate Batch Count
            if (acceptedCount >= Options.MaxFiles)
            {
                model.Status = TransferStatus.Skipped;
                model.StatusMessage = "Limit exceeded";
                await FireEventAsync(EventNotificationType.FileSkipped, model, "Max files reached");
                continue;
            }

            // Accepted
            acceptedCount++;
        }

        if (_transfers.All(t => t.IsFinished)) return; // Nothing to do

        // 4. Start the Pipeline
        await FireEventAsync(EventNotificationType.BatchStarted);
        _cpuChannel = Channel.CreateUnbounded<ProcessingJob>();

        // Fire and forget the background loops (they are managed by _batchCts)
        _ = RunNetworkProducerAsync(_batchCts.Token);
        _ = RunCpuConsumerAsync(_batchCts.Token);
    }

    public async Task CancelAllAsync()
    {
        _batchCts?.Cancel();

        foreach (var t in _transfers.Where(x => !x.IsFinished))
        {
            t.Status = TransferStatus.Cancelled;
            t.StatusMessage = "Batch Cancelled";
            await FireEventAsync(EventNotificationType.BatchCancelled);
        }
    }

    public async Task CancelFile(string fileId)
    {
        var model = _transfers.FirstOrDefault(x => x.Id == fileId);
        if (model != null && !model.IsFinished)
        {
            model.IndividualCts.Cancel();
            model.Status = TransferStatus.Cancelled;
            model.StatusMessage = "Cancelled";
            await FireEventAsync(EventNotificationType.FileCancelled, model);
        }
    }

    [JSInvokable]
    public void ReportUploadProgress(string batchId, int fileIndex, double percent)
    {
        //ZOMBIE CHECK: Safety: Ensure the progress report is for the current batch
        if (batchId != _currentBatchId) return;

        // Find the file by Index (which we mapped earlier)
        // Note: We use the Index property because ID isn't known to JS easily
        var model = _transfers.FirstOrDefault(x => x.Index == fileIndex);

        if (model != null && !model.IsFinished)
        {
            model.StageProgressPercent = percent;
            // Fire event to update UI
            // We use 'discard' here because this is a high-frequency call from JS
            _ = FireEventAsync(EventNotificationType.Progress, model);
        }
    }

    // --- PIPELINE STAGE 3: NETWORK PRODUCER (Uploads) ---
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
                // Link Batch Token + Individual File Token
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, model.IndividualCts.Token);
                var ct = linkedCts.Token;

                try
                {
                    // 1. Prepare
                    UpdateStage(model, TransferStage.Uploading, "Uploading...");

                    // 2. Generate Security Token (Just-In-Time)
                    // We bind the token to the UserID to prevent theft
                    var userId = UserId;
                    var uploadToken = SecurityService.GenerateToken(model.Id, userId);
                    model.UploadToken = uploadToken;
                    model.StartTime = DateTime.UtcNow;

                    // 3. JS Interop (The Upload)
                    // We await this, which throttles the network concurrency to 'MaxConcurrentUploads'
                    if (_jsModule != null)
                    {
                        var claimToken = await _jsModule.InvokeAsync<string>(
                            "uploadFile",
                            ct,
                            _dotNetRef,
                            InputSelector,
                            model.Index,
                            Options.UploadEndpointUrl,
                            _currentBatchId,
                            uploadToken,
                            TOKEN_HEADER_NAME, // Pass Header Name
                            _policyToken,      // Pass Encrypted Policy
                            POLICY_HEADER_NAME // Pass Policy Header Name
                            );

                        // 4. Handoff to CPU Consumer
                        if (!string.IsNullOrEmpty(claimToken))
                        {
                            // write the CLAIM TOKEN to the channel
                            await _cpuChannel!.Writer.WriteAsync(new ProcessingJob(model, claimToken), ct);

                            model.Stage = TransferStage.Hashing;
                            model.StatusMessage = "Queued for processing...";
                            await FireEventAsync(EventNotificationType.StageChange, model);
                        }
                    }
                }
                catch (OperationCanceledException) {
                    model.Status = TransferStatus.Cancelled;
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
            // Tell the consumer no more files are coming
            _cpuChannel?.Writer.TryComplete();
        }
    }

    // --- PIPELINE STAGE 5: CPU CONSUMER (Hashing & Saving) ---
    private async Task RunCpuConsumerAsync(CancellationToken batchToken)
    {
        if (_cpuChannel == null) return;

        // Separate concurrency limit for CPU tasks
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Options.MaxConcurrentProcessors,
            CancellationToken = batchToken
        };

        try
        {
            await Parallel.ForEachAsync(_cpuChannel.Reader.ReadAllAsync(batchToken), parallelOptions, async (job, token) =>
            {
                var model = job.Model;
                var claimToken = job.ClaimToken;

                // Link tokens again
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, model.IndividualCts.Token);
                var ct = linkedCts.Token;

                bool hasFile = Bridge.TryClaimFile(claimToken, out string? tempPath);

                if (!hasFile || string.IsNullOrEmpty(tempPath))
                {
                    FailModel(model, "Security Error: Unable to claim file from server bridge (Expired or Invalid Token).");
                    return;
                }

                try
                {
                    // A. Hashing (If enabled)
                    bool needsHashing = Options.EnabledFeatures.Contains(FileUploadFeature.VerifyLocalDuplicates) ||
                                        Options.EnabledFeatures.Contains(FileUploadFeature.VerifyRemoteDuplicates);

                    if (needsHashing)
                    {
                        UpdateStage(model, TransferStage.Hashing, "Verifying integrity...");
                        model.DetectedHash = await CalculateHashAsync(tempPath, model, ct);
                    }

                    // B. Local Duplication Check
                    if (Options.EnabledFeatures.Contains(FileUploadFeature.VerifyLocalDuplicates) && model.DetectedHash != null)
                    {
                        UpdateStage(model, TransferStage.VerifyingLocalDuplicates, "Checking batch...");
                        if (!_localHashCache.TryAdd(model.DetectedHash, model.FileName))
                        {
                            model.Status = TransferStatus.Skipped;
                            model.StatusMessage = "Duplicate in batch";
                            await FireEventAsync(EventNotificationType.FileSkipped, model);
                            return; // Skip saving
                        }
                    }

                    // C. Remote Duplication Check (User Callback)
                    if (Options.EnabledFeatures.Contains(FileUploadFeature.VerifyRemoteDuplicates) &&
                        model.DetectedHash != null &&
                        OnVerifyRemoteDuplicate != null)
                    {
                        UpdateStage(model, TransferStage.VerifyingRemoteDuplicates, "Checking server...");
                        bool exists = await OnVerifyRemoteDuplicate(model);
                        if (exists)
                        {
                            model.Status = TransferStatus.Skipped;
                            model.StatusMessage = "Already exists";
                            await FireEventAsync(EventNotificationType.FileSkipped, model);
                            return;
                        }
                    }

                    // D. Saving (User Callback)
                    if (Options.EnabledFeatures.Contains(FileUploadFeature.SaveToServer) && OnFileUploadCompleted != null)
                    {
                        UpdateStage(model, TransferStage.ServerSaving, "Saving...");

                        await using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var savingProgress = new ProgressStream(fs, read => UpdateThrottledProgress(model, read, model.FileSize));
                        await OnFileUploadCompleted(model, savingProgress);
                    }

                    // Success!
                    model.Status = TransferStatus.Completed;
                    model.StatusMessage = "Done";
                    model.EndTime = DateTime.UtcNow;
                    await FireEventAsync(EventNotificationType.FileCompleted, model);
                }
                catch (OperationCanceledException) { /* Ignored */ }
                catch (Exception ex)
                {
                    FailModel(model, $"Processing error: {ex.Message}");
                }
                finally
                {
                    // Cleanup Temp File
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            });
        }
        catch (OperationCanceledException) { /* Batch Cancelled */ }
        finally
        {
            await FireEventAsync(EventNotificationType.BatchCompleted);
        }
    }

    // --- HELPERS ---

    private async Task<string> CalculateHashAsync(string path, FileTransferData model, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hashProgress = new ProgressStream(fs, read => UpdateThrottledProgress(model, read, model.FileSize));
        byte[] hashBytes = await sha.ComputeHashAsync(hashProgress, ct);
        return Convert.ToBase64String(hashBytes);
    }

    private void UpdateStage(FileTransferData model, TransferStage stage, string msg)
    {
        model.Stage = stage;
        model.StatusMessage = msg;
        model.StageProgressPercent = 0; // Reset for new stage
        _ = FireEventAsync(EventNotificationType.StageChange, model);
    }

    private void FailModel(FileTransferData model, string error)
    {
        model.Status = TransferStatus.Failed;
        model.StatusMessage = error;
        _ = FireEventAsync(EventNotificationType.FileFailed, model);
    }

    private void UpdateThrottledProgress(FileTransferData model, long current, long total)
    {
        // Safety: If file is effectively done/dead, stop reporting progress
        if (model.IsFinished) return;

        var percent = total == 0 ? 0 : (double)current / total * 100;
        model.StageProgressPercent = percent;

        var now = DateTime.UtcNow;

        // Throttling Logic:
        // 1. Always fire if 100% (so we don't get stuck at 99%)
        // 2. Always fire if 0% (so the bar appears)
        // 3. Otherwise, check the Time Interval
        if (percent >= 100 || percent == 0 || (now - model.LastUIUpdate).TotalMilliseconds > Options.UIProgressUpdateIntervalMs)
        {
            model.LastUIUpdate = now;
            // Fire and forget (don't await in the stream callback)
            _ = FireEventAsync(EventNotificationType.Progress, model);
        }
    }

    private async Task FireEventAsync(EventNotificationType type, FileTransferData? model = null, string? msg = null)
    {
        // 1. If nobody is listening, don't do anything.
        if (!OnEvent.HasDelegate) return;

        try
        {
            // 2. Use ComponentBase.InvokeAsync
            // This automatically marshals execution to the UI thread (Dispatcher).
            // It waits for the UI to acknowledge receipt.
            await InvokeAsync(() => OnEvent.InvokeAsync(new FileNotificationEvent(type, model, msg)));
        }
        catch (ObjectDisposedException)
        {
            // Component was disposed while event was firing. Safe to ignore.
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected. Safe to ignore.
        }
        catch (TaskCanceledException)
        {
            // Task cancelled. Safe to ignore.
        }
    }


    public async ValueTask DisposeAsync()
    {
        _batchCts?.Cancel();
        _dotNetRef?.Dispose();
        if (_jsModule != null)
        {
            try { await _jsModule.DisposeAsync(); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}