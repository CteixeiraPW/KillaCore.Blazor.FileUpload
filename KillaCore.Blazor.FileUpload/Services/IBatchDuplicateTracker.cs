namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// Tracks file hashes and consumed upload tokens for the lifetime of a batch, preventing both
/// duplicate content within a batch and replay of a one-time upload token.
/// <para>
/// <b>Single-process only.</b> The built-in implementation is backed by <c>IMemoryCache</c>, so
/// across a web farm each server keeps its own state: a token can be replayed once per server,
/// and in-batch duplicate detection only sees the files that landed on the same node.
/// Deployments behind a load balancer should either use sticky sessions, or register their own
/// implementation over a distributed store offering an atomic insert-if-absent (for example
/// Redis <c>SET NX</c>). This interface is public for exactly that reason.
/// </para>
/// </summary>
public interface IBatchDuplicateTracker
{
    /// <summary>
    /// Registers a batch as legitimate and records the upload context that owns it.
    /// Called as soon as a valid, unused upload token is presented — before content
    /// validation — so a batch whose files all fail validation can still be completed.
    /// Idempotent: the first call for a given batch decides the context.
    /// </summary>
    void RegisterBatch(string batchId, string uploadContext);

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

    /// <summary>
    /// Returns true if the given batchId has had at least one file registered.
    /// Used to validate that a batch completion request is legitimate.
    /// </summary>
    bool IsBatchKnown(string batchId);

    /// <summary>
    /// Marks a batch as completed so it cannot be completed again, and reports the
    /// upload context recorded for it so the matching hooks can be resolved.
    /// Returns false if the batch is unknown or has already been completed (prevents replay).
    /// </summary>
    bool TryCompleteBatch(string batchId, out string? uploadContext);
}
