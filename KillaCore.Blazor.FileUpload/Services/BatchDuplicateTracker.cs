using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace KillaCore.Blazor.FileUpload.Services;

internal class BatchDuplicateTracker(IMemoryCache cache) : IBatchDuplicateTracker
{
    private static readonly object _tokenUsedMarker = new();

    public bool TryRegisterBatchHash(string batchId, string fileHash)
    {
        if (string.IsNullOrEmpty(batchId) || string.IsNullOrEmpty(fileHash))
            return true;

        string cacheKey = $"batch_hashes_{batchId}";

        var hashSet = cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new ConcurrentDictionary<string, byte>();
        });

        return hashSet!.TryAdd(fileHash, 1);
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

        return cache.TryGetValue($"batch_hashes_{batchId}", out _);
    }

    public bool TryCompleteBatch(string batchId)
    {
        if (string.IsNullOrEmpty(batchId))
            return false;

        // Ensure the batch actually had files uploaded
        if (!IsBatchKnown(batchId))
            return false;

        // Prevent completing the same batch twice
        string completionKey = $"batch_completed_{batchId}";

        bool isNew = false;
        cache.GetOrCreate(completionKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            isNew = true;
            return _tokenUsedMarker;
        });

        return isNew;
    }
}