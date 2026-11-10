using System.Net;

namespace KillaCore.Blazor.FileUpload.Tests;

/// <summary>
/// Limits are configured on the server and enforced there. Before this they lived only in the
/// browser, so every one of these passed by doing nothing.
/// </summary>
public class UploadLimitsTests
{
    [Fact]
    public async Task Oversized_file_is_rejected()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:MaxFileBytes"] = "1048576" // 1MB
        });

        var response = await host.UploadAsync(UploadTestHost.Pdf(2 * 1024 * 1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("1,048,576", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Hooks.Saved);
    }

    [Fact]
    public async Task File_within_the_limit_is_accepted()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:MaxFileBytes"] = "1048576"
        });

        var response = await host.UploadAsync(UploadTestHost.Pdf(512 * 1024));

        response.EnsureSuccessStatusCode();
        Assert.Equal("Saved", await UploadTestHost.OutcomeOf(response));
        Assert.Single(host.Hooks.Saved);
    }

    /// <summary>
    /// The configuration binder ADDS to a pre-populated List&lt;T&gt; instead of replacing it. A
    /// default list in the property initialiser therefore made a configured policy the union of
    /// the caller's list and the built-in one — configuring it WIDENED it. Found by inspecting a
    /// running server, not by any compiler.
    /// </summary>
    [Fact]
    public async Task Configured_mime_types_replace_the_defaults_instead_of_adding_to_them()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:AllowedMimeTypes:0"] = "image/png",
            ["KillaCoreFileUpload:AllowedMimeTypes:1"] = "image/jpeg"
        });

        var limits = await host.GetLimitsAsync("Default");

        Assert.Equal(["image/png", "image/jpeg"], limits.AllowedMimeTypes);
        Assert.DoesNotContain("application/pdf", limits.AllowedMimeTypes);
    }

    [Fact]
    public async Task Built_in_types_apply_when_nothing_is_configured()
    {
        await using var host = await UploadTestHost.StartAsync();

        var limits = await host.GetLimitsAsync("Default");

        Assert.Contains("application/pdf", limits.AllowedMimeTypes);
        Assert.DoesNotContain("text/html", limits.AllowedMimeTypes);
    }

    [Fact]
    public async Task A_type_outside_the_policy_is_rejected()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:AllowedMimeTypes:0"] = "image/png"
        });

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Hooks.Saved);
    }

    [Fact]
    public async Task A_profile_overrides_the_root_limit_for_its_context()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:MaxFileBytes"] = "1048576",
            ["KillaCoreFileUpload:Profiles:Large:MaxFileBytes"] = "8388608"
        });

        var payload = UploadTestHost.Pdf(2 * 1024 * 1024);

        var underProfile = await host.UploadAsync(payload, uploadContext: "Large");
        var underRoot = await host.UploadAsync(payload, uploadContext: "Default");

        underProfile.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, underRoot.StatusCode);
    }

    [Fact]
    public async Task A_profile_inherits_what_it_does_not_override()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:MaxFileBytes"] = "1048576",
            ["KillaCoreFileUpload:AllowedMimeTypes:0"] = "application/pdf",
            ["KillaCoreFileUpload:Profiles:Large:MaxFileBytes"] = "8388608"
        });

        var limits = await host.GetLimitsAsync("Large");

        Assert.Equal(8388608, limits.MaxFileBytes);
        Assert.Equal(["application/pdf"], limits.AllowedMimeTypes);
    }

    [Fact]
    public async Task An_unknown_context_falls_back_to_the_root_limits()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:MaxFileBytes"] = "4096"
        });

        var limits = await host.GetLimitsAsync("NoSuchContext");

        Assert.Equal(4096, limits.MaxFileBytes);
    }
}
