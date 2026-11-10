using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Extension;
using KillaCore.Blazor.FileUpload.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KillaCore.Blazor.FileUpload.Tests;

/// <summary>
/// A real ASP.NET host wrapped around the package, driven over a <see cref="TestServer"/>.
/// <para>
/// Deliberately not a mocked controller. Most of what is worth testing here lives in the pieces a
/// direct method call would skip — multipart parsing, the resource filter that rejects over-sized
/// requests before model binding, the authorization fallback policy, configuration binding. The
/// cost is a host per test; it starts in milliseconds.
/// </para>
/// </summary>
internal sealed class UploadTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _tempFolder;

    public HttpClient Client { get; }
    public RecordingHooks Hooks { get; }

    private UploadTestHost(WebApplication app, HttpClient client, RecordingHooks hooks, string tempFolder)
    {
        _app = app;
        _tempFolder = tempFolder;
        Client = client;
        Hooks = hooks;
    }

    public static async Task<UploadTestHost> StartAsync(
        IDictionary<string, string?>? settings = null,
        IUploadScanner? scanner = null,
        bool requireAuthenticatedUser = false,
        bool allowAnonymousUploads = false,
        RecordingHooks? hooks = null,
        bool registerHooks = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // Each host gets its own quarantine folder so parallel tests cannot see each other's files.
        var tempFolder = Path.Combine(Path.GetTempPath(), "KillaCoreTests", Guid.NewGuid().ToString("N"));

        var config = new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:SecretKey"] = "unit-test-secret-key-at-least-16-chars",
            ["KillaCoreFileUpload:TempFolder"] = tempFolder
        };

        if (settings is not null)
            foreach (var kv in settings) config[kv.Key] = kv.Value;

        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.AddKillaCoreFileUploadServer(
            builder.Configuration, allowAnonymousUploads: allowAnonymousUploads);

        hooks ??= new RecordingHooks();
        if (registerHooks) builder.Services.AddSingleton<IFileUploadServerHooks>(hooks);

        if (scanner is not null) builder.Services.AddSingleton(scanner);

        if (requireAuthenticatedUser)
        {
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, NeverAuthenticateHandler>("Test", _ => { });

            builder.Services.AddAuthorization(o =>
                o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        }

        var app = builder.Build();

        if (requireAuthenticatedUser)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapControllers();

        await app.StartAsync();

        return new UploadTestHost(app, app.GetTestClient(), hooks, tempFolder);
    }

    // --- Request helpers ---

    public async Task<string> GetTokenAsync(string fileId, string userId = "tester")
    {
        var response = await Client.GetAsync($"/kc-api/uploads/token/{fileId}?userId={userId}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    public async Task<HttpResponseMessage> UploadAsync(
        byte[] content,
        string fileName = "file.pdf",
        string contentType = "application/pdf",
        string? batchId = null,
        string? uploadContext = null,
        string? token = null,
        string? fileId = null)
    {
        fileId ??= Guid.NewGuid().ToString();
        token ??= await GetTokenAsync(fileId);
        batchId ??= Guid.NewGuid().ToString();

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, "/kc-api/uploads/temp") { Content = form };
        request.Headers.Add(FileUploadConstants.TOKEN_HEADER_NAME, token);
        request.Headers.Add(FileUploadConstants.BATCH_ID_HEADER_NAME, batchId);

        if (uploadContext is not null)
            request.Headers.Add(FileUploadConstants.CONTEXT_HEADER_NAME, uploadContext);

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> CompleteBatchAsync(string batchId, params FileTransferData[] files)
        => await Client.PostAsJsonAsync($"/kc-api/uploads/batch/{batchId}/complete", files);

    /// <summary>
    /// Reads the limits endpoint as a client would. Parsed into a local shape on purpose: a test
    /// that reused the server's own type could pass while the wire format was wrong.
    /// </summary>
    public async Task<TestLimits> GetLimitsAsync(string context)
    {
        var json = await Client.GetFromJsonAsync<JsonElement>($"/kc-api/uploads/policy/{context}");

        return new TestLimits(
            json.GetProperty("maxFileBytes").GetInt64(),
            [.. json.GetProperty("allowedMimeTypes").EnumerateArray().Select(x => x.GetString()!)]);
    }

    public static async Task<string?> OutcomeOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty("outcome", out var o) ? o.GetString() : null;
    }

    /// <summary>A file with a valid PDF signature, padded to the requested size.</summary>
    public static byte[] Pdf(int totalBytes, byte fill = 0x41, string extra = "")
    {
        var header = Encoding.ASCII.GetBytes("%PDF-1.4\n" + extra);
        var buffer = new byte[Math.Max(totalBytes, header.Length)];

        header.CopyTo(buffer, 0);
        Array.Fill(buffer, fill, header.Length, buffer.Length - header.Length);

        return buffer;
    }

    /// <summary>Bytes with no recognisable signature, for spoofing tests.</summary>
    public static byte[] NotAPdf(int totalBytes = 4096)
    {
        var buffer = new byte[totalBytes];
        Array.Fill(buffer, (byte)0x5A); // 'Z'
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        try { if (Directory.Exists(_tempFolder)) Directory.Delete(_tempFolder, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Leaves the request unauthenticated, so a fallback policy produces a challenge.</summary>
    private sealed class NeverAuthenticateHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }
}

/// <summary>The limits endpoint's response, as a caller sees it on the wire.</summary>
internal sealed record TestLimits(long MaxFileBytes, IReadOnlyList<string> AllowedMimeTypes);

/// <summary>
/// Hooks that record what the pipeline handed them, so a test can assert on what the server
/// decided rather than on what it reported.
/// </summary>
internal sealed class RecordingHooks : IFileUploadServerHooks
{
    private readonly Lock _gate = new();

    public List<FileTransferData> Saved { get; } = [];
    public List<(string BatchId, IReadOnlyList<FileTransferData> Files)> Batches { get; } = [];
    public HashSet<string> KnownHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> SavedBytes { get; } = [];

    public Task<bool> CheckRemoteDuplicateAsync(FileTransferData data, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(data.DetectedHash is not null && KnownHashes.Contains(data.DetectedHash));
        }
    }

    public async Task SaveFileAsync(FileTransferData data, Stream fileStream, CancellationToken ct)
    {
        // Drained deliberately: in direct-to-storage mode this is what computes the hash and size.
        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);

        data.FinalResourceId = Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            Saved.Add(data);
            SavedBytes[data.FileName] = buffer.ToArray();
            if (data.DetectedHash is not null) KnownHashes.Add(data.DetectedHash);
        }
    }

    public Task OnBatchCompletedAsync(string batchId, IReadOnlyList<FileTransferData> files)
    {
        lock (_gate) { Batches.Add((batchId, files)); }
        return Task.CompletedTask;
    }
}

/// <summary>A scanner with a fixed verdict, or one that always throws.</summary>
internal sealed class StubScanner(bool clean, string threat = "Test.Threat", bool throws = false) : IUploadScanner
{
    public int Calls { get; private set; }

    public Task<ScanResult> ScanAsync(FileTransferData data, Stream content, CancellationToken ct)
    {
        Calls++;

        if (throws) throw new InvalidOperationException("scanner unavailable");

        return Task.FromResult(clean ? ScanResult.Clean : ScanResult.Infected(threat));
    }
}
