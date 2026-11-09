using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace KillaCore.Blazor.FileUpload.Services;

internal class BatchDuplicateTracker(IMemoryCache cache) : IBatchDuplicateTracker
{
    internal const string DefaultContext = "Default";

    private static readonly object _tokenUsedMarker = new();

    private static readonly TimeSpan _batchLifespan = TimeSpan.FromHours(1);

    /// <summary>
    /// Per-batch state: the content hashes seen so far, plus the upload context that
    /// owns the batch. The context is captured server-side on the first request of the
    /// batch so the completion call cannot name a different one.
    /// </summary>
    private sealed class BatchState
    {
        public ConcurrentDictionary<string, byte> Hashes { get; } = new();
        public string UploadContext { get; init; } = DefaultContext;
    }

    private static string BatchKey(string batchId) => $"batch_hashes_{batchId}";

    private BatchState GetOrCreateBatch(string batchId, string uploadContext)
    {
        return cache.GetOrCreate(BatchKey(batchId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _batchLifespan;
            return new BatchState { UploadContext = uploadContext };
        })!;
    }

    public void RegisterBatch(string batchId, string uploadContext)
    {
        if (string.IsNullOrEmpty(batchId))
            return;

        GetOrCreateBatch(
            batchId,
            string.IsNullOrWhiteSpace(uploadContext) ? DefaultContext : uploadContext);
    }

    public bool TryRegisterBatchHash(string batchId, string fileHash)
    {
        if (string.IsNullOrEmpty(batchId) || string.IsNullOrEmpty(fileHash))
            return true;

        // The batch is normally created by RegisterBatch earlier in the request;
        // create defensively so a hash is never silently dropped.
        var state = GetOrCreateBatch(batchId, DefaultContext);

        return state.Hashes.TryAdd(fileHash, 1);
    }

    public bool RegisterUsedToken(string secureToken)
    {
        if (string.IsNullOrWhiteSpace(secureToken))
            return false;

        bool isNew = false;

        cache.GetOrCreate(secureToken, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            isNew = true;
            return _tokenUsedMarker;
        });

        return isNew;
    }

    public bool IsBatchKnown(string batchId)
    {
        if (string.IsNullOrEmpty(batchId))
            return false;

        return cache.TryGetValue(BatchKey(batchId), out _);
    }

    public bool TryCompleteBatch(string batchId, out string? uploadContext)
    {
        uploadContext = null;

        if (string.IsNullOrEmpty(batchId))
            return false;

        // Ensure the batch actually came through our pipeline
        if (!cache.TryGetValue(BatchKey(batchId), out BatchState? state) || state is null)
            return false;

        // Prevent completing the same batch twice
        string completionKey = $"batch_completed_{batchId}";

        bool isNew = false;
        cache.GetOrCreate(completionKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _batchLifespan;
            isNew = true;
            return _tokenUsedMarker;
        });

        if (!isNew)
            return false;

        uploadContext = state.UploadContext;
        return true;
    }
}
