using KillaCore.Blazor.FileUpload.Client.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace KillaCore.Blazor.FileUpload.Services;

internal class BatchDuplicateTracker(IMemoryCache cache) : IBatchDuplicateTracker
{
    internal const string DefaultContext = "Default";

    private static readonly object _usedMarker = new();

    private static readonly TimeSpan _batchLifespan = TimeSpan.FromHours(1);
    private static readonly TimeSpan _tokenLifespan = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Guards the cache's read-then-write sequences.
    /// <para>
    /// <see cref="IMemoryCache"/> offers no atomic test-and-set: <c>GetOrCreate</c> can run its
    /// factory on two threads at once, so two concurrent requests bearing the SAME upload token
    /// could both be told they were the first — which is precisely the replay this class exists
    /// to prevent, and concurrent requests are the norm given MaxConcurrentUploads.
    /// </para>
    /// <para>
    /// Each critical section is a dictionary probe plus an insert, measured in microseconds
    /// against uploads measured in seconds, so serialising them costs nothing worth having.
    /// </para>
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Per-batch state: the content hashes seen so far, plus the upload context that owns the
    /// batch. The context is captured server-side on the batch's first request so the completion
    /// call cannot name a different one.
    /// </summary>
    private sealed class BatchState
    {
        public ConcurrentDictionary<string, byte> Hashes { get; } = new();

        /// <summary>The server's own record of each processed file, keyed by its Id.</summary>
        public ConcurrentDictionary<string, FileTransferData> Files { get; } =
            new(StringComparer.Ordinal);

        public string UploadContext { get; init; } = DefaultContext;
    }

    private static string BatchKey(string batchId) => $"batch_hashes_{batchId}";

    private static string CompletionKey(string batchId) => $"batch_completed_{batchId}";

    private BatchState GetOrCreateBatch(string batchId, string uploadContext)
    {
        var key = BatchKey(batchId);

        // Fast path: an existing batch needs no lock, since IMemoryCache reads are thread-safe.
        if (cache.TryGetValue(key, out BatchState? existing) && existing is not null)
            return existing;

        lock (_gate)
        {
            // Re-check: another thread may have created it between the probe and the lock.
            if (cache.TryGetValue(key, out BatchState? current) && current is not null)
                return current;

            var created = new BatchState { UploadContext = uploadContext };
            cache.Set(key, created, _batchLifespan);
            return created;
        }
    }

    public void RegisterBatch(string batchId, string uploadContext)
    {
        if (string.IsNullOrEmpty(batchId))
            return;

        GetOrCreateBatch(
            batchId,
            string.IsNullOrWhiteSpace(uploadContext) ? DefaultContext : uploadContext);
    }

    public void RecordFile(string batchId, FileTransferData data)
    {
        if (string.IsNullOrEmpty(batchId) || string.IsNullOrEmpty(data.Id))
            return;

        var state = GetOrCreateBatch(batchId, DefaultContext);
        state.Files[data.Id] = data;
    }

    public bool TryRegisterBatchHash(string batchId, string fileHash)
    {
        if (string.IsNullOrEmpty(batchId) || string.IsNullOrEmpty(fileHash))
            return true;

        // The batch is normally created by RegisterBatch earlier in the request; create
        // defensively so a hash is never silently dropped.
        var state = GetOrCreateBatch(batchId, DefaultContext);

        // ConcurrentDictionary.TryAdd is already atomic, so this stays outside the lock.
        return state.Hashes.TryAdd(fileHash, 1);
    }

    public bool RegisterUsedToken(string secureToken)
    {
        if (string.IsNullOrWhiteSpace(secureToken))
            return false;

        lock (_gate)
        {
            if (cache.TryGetValue(secureToken, out _))
                return false;

            cache.Set(secureToken, _usedMarker, _tokenLifespan);
            return true;
        }
    }

    public bool IsBatchKnown(string batchId)
    {
        if (string.IsNullOrEmpty(batchId))
            return false;

        return cache.TryGetValue(BatchKey(batchId), out _);
    }

    public bool TryCompleteBatch(string batchId, out BatchCompletion? completion)
    {
        completion = null;

        if (string.IsNullOrEmpty(batchId))
            return false;

        lock (_gate)
        {
            // Ensure the batch actually came through our pipeline.
            if (!cache.TryGetValue(BatchKey(batchId), out BatchState? state) || state is null)
                return false;

            // Prevent completing the same batch twice.
            if (cache.TryGetValue(CompletionKey(batchId), out _))
                return false;

            cache.Set(CompletionKey(batchId), _usedMarker, _batchLifespan);

            completion = new BatchCompletion(
                state.UploadContext,
                [.. state.Files.Values]);

            return true;
        }
    }
}
