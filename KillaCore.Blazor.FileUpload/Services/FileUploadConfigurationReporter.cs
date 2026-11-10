using KillaCore.Blazor.FileUpload.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KillaCore.Blazor.FileUpload.Services;

/// <summary>
/// Records which configuration section the limits were bound from, and whether any limit keys
/// were actually present there.
/// </summary>
internal sealed record FileUploadConfigState(string SectionName, bool LimitsConfigured);

/// <summary>
/// Reports the effective upload limits once at startup.
/// <para>
/// Upload limits are enforced server-side, and the built-in defaults are deliberately
/// restrictive, so an application that never configures them fails closed. Failing closed
/// silently is its own problem — an upgrade would start rejecting files with no hint why — so
/// this says so in the log where a developer sees it, rather than in production.
/// </para>
/// </summary>
internal sealed class FileUploadConfigurationReporter(
    IOptions<FileUploadServerOptions> options,
    FileUploadConfigState state,
    IServiceProvider serviceProvider,
    ILogger<FileUploadConfigurationReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GuardUnscannableContexts();

        var limits = options.Value.GetEffectiveLimits(null);

        if (!state.LimitsConfigured)
        {
            logger.LogWarning(
                "KillaCore upload limits are not configured; falling back to built-in defaults " +
                "({MaxBytes:N0} bytes, {TypeCount} allowed MIME type(s)). Set {Section}:MaxFileBytes " +
                "and {Section}:AllowedMimeTypes to control them.",
                limits.MaxFileBytes, limits.AllowedMimeTypes.Count, state.SectionName, state.SectionName);
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "KillaCore upload limits: {MaxBytes:N0} bytes per file, {TypeCount} allowed MIME type(s), " +
                "{ProfileCount} context profile(s), {MaxRequest:N0} byte transport ceiling.",
                limits.MaxFileBytes,
                limits.AllowedMimeTypes.Count,
                options.Value.Profiles.Count,
                options.Value.MaxRequestBytes);
        }

        // A per-file limit above the transport ceiling can never be reached: the request is
        // refused mid-stream before the per-file check ever runs.
        long largestPerFileLimit = options.Value.Profiles.Values
            .Select(p => p.MaxFileBytes ?? options.Value.MaxFileBytes)
            .Append(options.Value.MaxFileBytes)
            .Max();

        if (largestPerFileLimit > options.Value.MaxRequestBytes)
        {
            logger.LogWarning(
                "KillaCore MaxRequestBytes ({MaxRequest:N0}) is below the largest per-file limit " +
                "({Largest:N0}). Files between the two are rejected by the transport before the " +
                "per-file check applies, so that limit is unreachable.",
                options.Value.MaxRequestBytes, largestPerFileLimit);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Refuses to start when a context both streams directly to storage and has a scanner.
    /// <para>
    /// Direct mode keeps no quarantine copy, so there is nothing for a scanner to examine before
    /// the file is live. Starting anyway would store unscanned files while the configuration
    /// looked like it was scanning them — a failure nobody notices until it matters. Better to
    /// fail here, where the message is read.
    /// </para>
    /// </summary>
    private void GuardUnscannableContexts()
    {
        var o = options.Value;

        foreach (var (context, direct) in EnumerateContexts(o))
        {
            if (!direct) continue;

            var scanner = serviceProvider.GetKeyedService<IUploadScanner>(context)
                          ?? serviceProvider.GetService<IUploadScanner>();

            if (scanner is null) continue;

            throw new InvalidOperationException(
                $"Upload context '{context}' sets StreamDirectToStorage and also resolves an " +
                $"IUploadScanner ({scanner.GetType().Name}). Direct mode holds no quarantine copy, " +
                "so the file cannot be scanned before it reaches storage. Either turn off " +
                "StreamDirectToStorage for this context, or do not register a scanner for it.");
        }
    }

    private static IEnumerable<(string Context, bool Direct)> EnumerateContexts(FileUploadServerOptions o)
    {
        yield return ("Default", o.StreamDirectToStorage);

        foreach (var profile in o.Profiles)
            yield return (profile.Key, profile.Value.StreamDirectToStorage ?? o.StreamDirectToStorage);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
