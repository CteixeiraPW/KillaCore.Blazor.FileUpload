using KillaCore.Blazor.FileUpload.Client.Models;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// What the server knows about a finished batch: the context that owned it, and the server's own
/// record of each file that actually arrived.
/// </summary>
/// <param name="UploadContext">The context captured on the batch's first request.</param>
/// <param name="ServerRecords">
/// One entry per file the server processed, carrying server-computed values such as the SHA-256.
/// Files the client skipped before uploading are not here — the server never saw them.
/// </param>
public sealed record BatchCompletion(string UploadContext, IReadOnlyList<FileTransferData> ServerRecords);

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
    /// Stores the server's own record of a processed file, keyed by its <c>Id</c>.
    /// <para>
    /// The batch-completion payload is posted by the client, which never learns the server-side
    /// SHA-256, so without this the hash would arrive null in <c>OnBatchCompletedAsync</c>.
    /// Echoing the client's own value back would be worse: the server would be trusting a number
    /// it is supposed to be computing.
    /// </para>
    /// </summary>
    void RecordFile(string batchId, FileTransferData data);

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
    /// Marks a batch as completed so it cannot be completed again, and reports what the server
    /// knows about it. Returns false if the batch is unknown or has already been completed.
    /// </summary>
    bool TryCompleteBatch(string batchId, out BatchCompletion? completion);
}
