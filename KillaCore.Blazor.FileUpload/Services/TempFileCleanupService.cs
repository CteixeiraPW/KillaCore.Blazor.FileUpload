using KillaCore.Blazor.FileUpload.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// A background worker that guarantees orphaned temp files are deleted 
/// in the event of a hard server crash or application pool recycle.
/// </summary>
internal class TempFileCleanupService : BackgroundService
{

    private readonly ILogger<TempFileCleanupService> _logger;


    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "KillaCoreUploads");

    // How long a file is allowed to exist before it is considered an "orphan"
    private readonly TimeSpan _maxFileAge = TimeSpan.FromHours(1);

    // How often the janitor wakes up to check the folder
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    public TempFileCleanupService(
        ILogger<TempFileCleanupService> logger,
        IOptions<FileUploadServerOptions> options)
    {
        _logger = logger;

        // Determine the actual path
        string folderConfig = options.Value.TempFolder;
        if (string.IsNullOrWhiteSpace(folderConfig)) folderConfig = "KillaCoreUploads";

        _tempRoot = Path.IsPathRooted(folderConfig)
            ? folderConfig
            : Path.Combine(Path.GetTempPath(), folderConfig);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on startup, then loop based on the interval
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanOrphanedFiles();
            }
            catch (Exception ex)
            {
                // Catch all so the background service never crashes the main app
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Failed to clean up temporary upload files.");
            }

            // Go to sleep until the next check
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private void CleanOrphanedFiles()
    {
        if (!Directory.Exists(_tempRoot)) return;

        var tempFiles = Directory.GetFiles(_tempRoot, "*.tmp");
        int deletedCount = 0;

        foreach (var file in tempFiles)
        {
            var fileInfo = new FileInfo(file);

            // If the file is older than our max age, it means the server crashed 
            // while it was uploading, or the memory cache failed to evict it.
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _maxFileAge)
            {
                try
                {
                    fileInfo.Delete();
                    deletedCount++;
                }
                catch (IOException)
                {
                    // File might be locked by an anti-virus, just skip and try again next time
                }
            }
        }

        if (deletedCount > 0 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("KillaCore File Janitor cleaned up {Count} orphaned temporary files.", deletedCount);
        }
    }
}