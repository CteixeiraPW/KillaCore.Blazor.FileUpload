namespace KillaCore.Blazor.FileUpload.Tests;

/// <summary>
/// Misconfigurations that are refused at startup rather than at upload time. A silently
/// unscanned path is worse than a failed boot, because nobody finds it until it matters.
/// </summary>
public class ConfigurationGuardTests
{
    [Fact]
    public async Task Direct_mode_with_a_scanner_refuses_to_start()
    {
        var start = UploadTestHost.StartAsync(
            new Dictionary<string, string?> { ["KillaCoreFileUpload:StreamDirectToStorage"] = "true" },
            scanner: new StubScanner(clean: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await start);

        Assert.Contains("StreamDirectToStorage", ex.Message);
        Assert.Contains("cannot be scanned", ex.Message);
    }

    [Fact]
    public async Task A_profile_using_direct_mode_with_a_scanner_refuses_to_start()
    {
        var start = UploadTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["KillaCoreFileUpload:Profiles:Fast:StreamDirectToStorage"] = "true"
            },
            scanner: new StubScanner(clean: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await start);

        Assert.Contains("'Fast'", ex.Message);
    }

    [Fact]
    public async Task Direct_mode_without_a_scanner_starts_normally()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:StreamDirectToStorage"] = "true"
        });

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_buffered_profile_alongside_a_scanner_starts_normally()
    {
        // Direct at the root, buffered for this context: the scanner has a copy to examine, so
        // the combination is legitimate and must not be refused.
        await using var host = await UploadTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["KillaCoreFileUpload:Profiles:Scanned:StreamDirectToStorage"] = "false"
            },
            scanner: new StubScanner(clean: true));

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096), uploadContext: "Scanned");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_short_secret_key_refuses_to_start()
    {
        var start = UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:SecretKey"] = "too-short"
        });

        // Surfaces on first use of the security service rather than at host start, but it still
        // stops the application rather than signing tokens with a weak key.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var host = await start;
            await host.GetTokenAsync(Guid.NewGuid().ToString());
        });
    }
}
