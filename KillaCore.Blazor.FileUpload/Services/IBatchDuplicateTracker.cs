namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// Tracks file hashes concurrently during an active upload batch to prevent 
/// the user from uploading identical file contents simultaneously.
/// </summary>
public interface IBatchDuplicateTracker
{
    /// <summary>
    /// Attempts to register a file's hash for the current batch.
    /// Returns false if another file in the exact same batch already registered this hash.
    /// </summary>
    bool TryRegisterBatchHash(string batchId, string fileHash);

    /// <summary>
    /// Registers a security token as used. 
    /// Returns false if the token has already been claimed (Replay Attack).
    /// </summary>
    bool RegisterUsedToken(string secureToken);
}
