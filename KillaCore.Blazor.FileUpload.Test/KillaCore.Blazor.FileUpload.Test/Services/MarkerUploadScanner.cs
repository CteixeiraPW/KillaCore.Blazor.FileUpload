using System.Text;
using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Services;

namespace KillaCore.Blazor.FileUpload.Test.Services;

/// <summary>
/// Stand-in for a real virus scanner: flags any file whose first 8KB contain a marker string.
/// A production implementation would stream <paramref name="content"/> to clamd, an ICAP service
/// or Defender instead — or, when the quarantine store and the scanner share a backend, read
/// <c>data.QuarantineHandle</c> and scan in place without moving the bytes at all.
/// <para>
/// Deliberately NOT the EICAR test signature. That string is detected by real antivirus, so a
/// fixture containing it would be quarantined by the developer's own machine before this code
/// ever saw it, and the test would fail for reasons that look nothing like the cause.
/// </para>
/// </summary>
public sealed class MarkerUploadScanner(ILogger<MarkerUploadScanner> logger) : IUploadScanner
{
    private static readonly byte[] _marker = Encoding.ASCII.GetBytes("KILLACORE-TEST-THREAT");

    public async Task<ScanResult> ScanAsync(FileTransferData data, Stream content, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        int read = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);

        if (buffer.AsSpan(0, read).IndexOf(_marker) >= 0)
        {
            logger.LogWarning("Scanner flagged {Name} ({Size} bytes).", data.FileName, data.FileSize);
            return ScanResult.Infected("KillaCore.Test.Marker");
        }

        return ScanResult.Clean;
    }
}
