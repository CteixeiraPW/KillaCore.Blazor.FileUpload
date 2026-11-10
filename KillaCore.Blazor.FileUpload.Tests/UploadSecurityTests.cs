using System.Net;

namespace KillaCore.Blazor.FileUpload.Tests;

public class UploadSecurityTests
{
    /// <summary>
    /// The upload action used to carry [AllowAnonymous], which is explicit authorization metadata
    /// and therefore short-circuited the host's FallbackPolicy: an application that required an
    /// authenticated user everywhere still had its file-receiving endpoint open, and could not
    /// close it without replacing the controller.
    /// <para>
    /// This could never be checked in the browser — the demo has no authorization middleware at
    /// all — so it was the main thing shipped on reasoning alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Upload_requires_authentication_when_the_host_sets_a_fallback_policy()
    {
        await using var host = await UploadTestHost.StartAsync(requireAuthenticatedUser: true);

        // An explicit token, so the helper does not call the (equally protected) token endpoint.
        var response = await host.UploadAsync(UploadTestHost.Pdf(4096), token: "irrelevant");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Hooks.Saved);
    }

    [Fact]
    public async Task Upload_stays_anonymous_when_the_host_opts_in()
    {
        await using var host = await UploadTestHost.StartAsync(
            requireAuthenticatedUser: true, allowAnonymousUploads: true);

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        response.EnsureSuccessStatusCode();
        Assert.Single(host.Hooks.Saved);
    }

    [Fact]
    public async Task Upload_is_open_when_the_host_configures_no_authorization()
    {
        // The common case, and the one that must not regress: an app with no auth keeps working.
        await using var host = await UploadTestHost.StartAsync();

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_token_cannot_be_used_twice()
    {
        await using var host = await UploadTestHost.StartAsync();

        var fileId = Guid.NewGuid().ToString();
        var token = await host.GetTokenAsync(fileId);
        var payload = UploadTestHost.Pdf(4096);

        var first = await host.UploadAsync(payload, token: token, fileId: fileId);
        var second = await host.UploadAsync(payload, token: token, fileId: fileId);

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("already been used", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_invalid_token_is_rejected()
    {
        await using var host = await UploadTestHost.StartAsync();

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096), token: "not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Content_that_does_not_match_the_extension_is_rejected()
    {
        await using var host = await UploadTestHost.StartAsync();

        // Declares application/pdf and is named .pdf, but carries no PDF signature.
        var response = await host.UploadAsync(UploadTestHost.NotAPdf(), fileName: "forged.pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Hooks.Saved);
    }

    [Fact]
    public async Task An_empty_file_is_rejected()
    {
        await using var host = await UploadTestHost.StartAsync();

        var response = await host.UploadAsync([], fileName: "empty.pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
