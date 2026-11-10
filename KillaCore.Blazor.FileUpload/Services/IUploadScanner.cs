using KillaCore.Blazor.FileUpload.Client.Models;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// The verdict on a quarantined file.
/// </summary>
/// <param name="IsClean">False rejects the file; it never reaches permanent storage.</param>
/// <param name="Threat">
/// What was found, shown to the user and recorded in the batch records. Keep it to a signature
/// name — it is attacker-influenced text that ends up in a browser and in your logs.
/// </param>
public sealed record ScanResult(bool IsClean, string? Threat = null)
{
    public static ScanResult Clean { get; } = new(true);

    public static ScanResult Infected(string threat) => new(false, threat);
}

/// <summary>
/// Inspects a file while it sits in quarantine, before it reaches permanent storage.
/// <para>
/// Optional. Register it like the server hooks — keyed by upload context, or unkeyed as a
/// fallback — and only contexts that resolve a scanner are scanned.
/// </para>
/// <para>
/// This runs after the duplicate checks and before <c>SaveFileAsync</c>, which is the only point
/// where a file both exists in full and is not yet reachable. It is why the quarantine exists,
/// and why a context using <c>StreamDirectToStorage</c> cannot be scanned: there is no held copy,
/// so the alternative would be writing the file to storage and deleting it afterwards — leaving
/// a window in which an infected file is live. Configuring both is refused at startup.
/// </para>
/// </summary>
public interface IUploadScanner
{
    /// <summary>
    /// Returns the verdict for <paramref name="content"/>, a readable stream over the quarantined
    /// file. <c>data.QuarantineHandle</c> is also set, so a scanner whose service can read the
    /// store directly may use that and ignore the stream.
    /// <para>
    /// <b>Throwing rejects the upload.</b> If your scanner is unreachable the file is refused
    /// rather than waved through, because the alternative is storing unscanned files precisely
    /// when the defence is down. A deployment that prefers to accept during an outage should
    /// catch internally and return <see cref="ScanResult.Clean"/>, making that choice explicit.
    /// </para>
    /// </summary>
    Task<ScanResult> ScanAsync(FileTransferData data, Stream content, CancellationToken ct);
}
