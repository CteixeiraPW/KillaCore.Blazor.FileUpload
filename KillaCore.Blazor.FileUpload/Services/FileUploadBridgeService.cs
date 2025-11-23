using System.Collections.Concurrent;

namespace KillaCore.Blazor.FileUpload.Services;

internal class FileUploadBridgeService : IFileUploadBridgeService, IDisposable
{
    // STORAGE 1: Files waiting to be processed by Blazor
    // Key: Token, Value: (Path, Expiration)
    private readonly ConcurrentDictionary<string, FileEntry> _pendingFiles = new();

    // STORAGE 2: Used Security Keys (Anti-Replay)
    // Key: UniqueID, Value: Expiration
    private readonly ConcurrentDictionary<string, DateTime> _usedKeys = new();

    private readonly Timer _cleanupTimer;

    // Config: How long a file sits on disk before we delete it as "abandoned"
    private readonly TimeSpan _fileRetentionPeriod = TimeSpan.FromMinutes(20);

    // Config: How long we remember a used key (should match your 5-min token lifetime)
    private readonly TimeSpan _keyRetentionPeriod = TimeSpan.FromMinutes(6);

    public FileUploadBridgeService()
    {
        // Run cleanup every 1 minute
        _cleanupTimer = new Timer(ExecuteCleanup, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // --- PART 1: FILE HANDOVER LOGIC ---

    public void RegisterFile(string token, string tempFilePath)
    {
        var entry = new FileEntry(tempFilePath, DateTime.UtcNow.Add(_fileRetentionPeriod));
        _pendingFiles.TryAdd(token, entry);
    }

    public bool TryClaimFile(string token, out string? tempFilePath)
    {
        tempFilePath = null;

        // Atomic Operation: Try to Get AND Remove.
        // This ensures that even if called in parallel, only one thread gets the file.
        if (_pendingFiles.TryRemove(token, out var entry))
        {
            // Check if it expired before we grabbed it
            if (DateTime.UtcNow > entry.ExpiresAt)
            {
                // It's expired. Delete the physical file immediately.
                SafeDeleteFile(entry.FilePath);
                return false;
            }

            tempFilePath = entry.FilePath;
            return true;
        }

        return false;
    }

    // --- PART 2: REPLAY PROTECTION LOGIC ---

    public bool RegisterUsedKey(string uniqueId)
    {
        var expiry = DateTime.UtcNow.Add(_keyRetentionPeriod);

        // TryAdd returns FALSE if the key already exists.
        // This is the core of the "One-Time Use" check.
        return _usedKeys.TryAdd(uniqueId, expiry);
    }

    // --- PART 3: GARBAGE COLLECTION (The "Janitor") ---

    private void ExecuteCleanup(object? state)
    {
        var now = DateTime.UtcNow;

        // 1. Clean up Abandoned Files
        foreach (var kvp in _pendingFiles)
        {
            if (now > kvp.Value.ExpiresAt)
            {
                // Remove from dictionary
                if (_pendingFiles.TryRemove(kvp.Key, out var entry))
                {
                    // CRITICAL: Delete the actual file from the disk
                    SafeDeleteFile(entry.FilePath);
                }
            }
        }

        // 2. Clean up Old Replay Keys (Just memory cleanup)
        foreach (var kvp in _usedKeys)
        {
            if (now > kvp.Value)
            {
                _usedKeys.TryRemove(kvp.Key, out _);
            }
        }
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
            // In a real library, you might want to log this internally.
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    // Helper Record
    private record FileEntry(string FilePath, DateTime ExpiresAt);
}