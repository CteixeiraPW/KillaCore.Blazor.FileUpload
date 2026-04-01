using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace KillaCore.Blazor.FileUpload.Services;

internal class FileUploadBridgeService(IMemoryCache cache) : IFileUploadBridgeService
{

    // Config: How long a file sits on disk before we delete it as "abandoned"
    private readonly TimeSpan _fileRetentionPeriod = TimeSpan.FromMinutes(20);

    // Config: How long we remember a used key (should match your 5-min token lifetime)
    private readonly TimeSpan _keyRetentionPeriod = TimeSpan.FromMinutes(6);

    // We keep ConcurrentDictionary for keys to guarantee a 100% thread-safe atomic TryAdd 
    // to strictly prevent race conditions in replay attacks.
    private readonly ConcurrentDictionary<string, byte> _usedKeys = new();

    // --- PART 1: FILE HANDOVER LOGIC ---

    public void RegisterFile(string token, string tempFilePath)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _fileRetentionPeriod
        };

        // This tells the framework: "When this entry expires, run this method"
        options.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            // FIXED: Changed CacheEntryRemovedReason to EvictionReason
            // Only delete the file if it was evicted due to Expiration or Memory Pressure.
            // DO NOT delete it if the reason is 'Removed' (which means the user successfully claimed it).
            if (reason != EvictionReason.Removed && value is string path)
            {
                SafeDeleteFile(path);
            }
        });

        cache.Set($"file_{token}", tempFilePath, options);
    }

    public bool TryClaimFile(string token, out string? tempFilePath)
    {
        string cacheKey = $"file_{token}";

        if (cache.TryGetValue(cacheKey, out string? path))
        {
            tempFilePath = path;

            // Remove it from the cache so it can't be claimed a second time.
            // This triggers the eviction callback with reason = 'Removed', so the file is kept safe.
            cache.Remove(cacheKey);
            return true;
        }

        tempFilePath = null;
        return false;
    }

    // --- PART 2: REPLAY PROTECTION LOGIC ---

    public bool RegisterUsedKey(string uniqueId)
    {
        // 1. Strict atomic check to prevent simultaneous requests from using the same token
        if (!_usedKeys.TryAdd(uniqueId, 1))
        {
            return false; // Key was already used!
        }

        // 2. Delegate the cleanup schedule to IMemoryCache
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _keyRetentionPeriod
        };

        options.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            // When the cache timer expires, silently clean up the ConcurrentDictionary
            var tokenKey = key?.ToString()?.Replace("key_", "");
            if (tokenKey != null)
            {
                _usedKeys.TryRemove(tokenKey, out _);
            }
        });

        cache.Set($"key_{uniqueId}", true, options);

        return true;
    }

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


    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Suppress errors. The file might be locked or already gone.
        }
    }
}