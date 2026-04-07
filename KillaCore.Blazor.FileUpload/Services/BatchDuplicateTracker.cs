using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace KillaCore.Blazor.FileUpload.Services;

internal class BatchDuplicateTracker(IMemoryCache cache) : IBatchDuplicateTracker
{
    public bool TryRegisterBatchHash(string batchId, string fileHash)
    {
        if (string.IsNullOrEmpty(batchId) || string.IsNullOrEmpty(fileHash))
            return true; // Skip check if data is missing

        string cacheKey = $"batch_hashes_{batchId}";

        // 1. Get or create a thread-safe dictionary for this specific Batch ID.
        // We use IMemoryCache so it automatically deletes itself after an hour!
        var hashSet = cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new ConcurrentDictionary<string, byte>();
        });

        // 2. TryAdd returns FALSE if the hash already exists in the dictionary.
        // This means another concurrent API request in this batch already uploaded it!
        return hashSet!.TryAdd(fileHash, 1);
    }

    public bool RegisterUsedToken(string secureToken)
    {
        if (string.IsNullOrWhiteSpace(secureToken))
            return false;

        // TryGetValue checks if the key exists. If it does, someone already used this token!
        if (cache.TryGetValue(secureToken, out _))
        {
            return false;
        }

        // If it doesn't exist, we add it to the cache.
        // We set the expiration to 5 minutes, perfectly matching your HMAC token lifespan.
        var options = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5));

        // The value "true" doesn't matter, we only care about the Key existing in memory.
        cache.Set(secureToken, true, options);

        return true;
    }
}