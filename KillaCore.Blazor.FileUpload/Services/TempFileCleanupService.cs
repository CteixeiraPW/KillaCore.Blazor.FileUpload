using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// Periodically asks the quarantine store to discard entries abandoned by a hard crash or an
/// application-pool recycle, which would otherwise accumulate indefinitely.
/// <para>
/// The sweep itself belongs to the store — only it knows what an orphan looks like, and a store
/// whose backing service expires objects on its own has nothing to do here.
/// </para>
/// </summary>
internal sealed class TempFileCleanupService(
    IUploadQuarantineStore quarantine,
    ILogger<TempFileCleanupService> logger) : BackgroundService
{
    /// <summary>How long an entry may exist before no live request could still be writing it.</summary>
    private static readonly TimeSpan _maxEntryAge = TimeSpan.FromHours(1);

    private static readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on startup, then loop based on the interval
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await quarantine.SweepOrphansAsync(_maxEntryAge, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // Catch all so the background service never crashes the main app
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Failed to sweep orphaned uploads from the quarantine store.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
