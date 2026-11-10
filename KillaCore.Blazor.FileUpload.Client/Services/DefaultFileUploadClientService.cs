using KillaCore.Blazor.FileUpload.Client.Models;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;

namespace KillaCore.Blazor.FileUpload.Client.Services;

public class DefaultFileUploadClientService : IFileUploadClientService
{
    private readonly HttpClient _httpClient;

    public DefaultFileUploadClientService(HttpClient httpClient, NavigationManager navManager)
    {
        _httpClient = httpClient;

        // Fallback only. Routes resolved from FileProcessingOptions.ServerBaseUrl come back
        // absolute and override this; relative ones resolve against it, which keeps apps hosted
        // under a sub-path working. Assigned conditionally so an explicit BaseAddress supplied
        // through AddKillaCoreFileUploadClient(configureClient) still wins.
        _httpClient.BaseAddress ??= new Uri(navManager.BaseUri);
    }

    public async Task<string> GetPolicyTokenAsync(FileProcessingOptions options, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync(
            options.ResolveApiUrl("policy"), options.AllowedMimeTypes, ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        return result?.Token ?? string.Empty;
    }

    public async Task<string> GetUploadTokenAsync(FileProcessingOptions options, string fileId, string userId, CancellationToken ct)
    {
        var route = options.ResolveApiUrl(
            $"token/{Uri.EscapeDataString(fileId)}?userId={Uri.EscapeDataString(userId)}");

        var response = await _httpClient.GetAsync(route, ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        return result?.Token ?? string.Empty;
    }

    public async Task NotifyBatchCompletedAsync(FileProcessingOptions options, string batchId, IEnumerable<FileTransferData> files, CancellationToken ct = default)
    {
        var route = options.ResolveApiUrl($"batch/{Uri.EscapeDataString(batchId)}/complete");

        var response = await _httpClient.PostAsJsonAsync(route, files, ct);
        response.EnsureSuccessStatusCode();
    }

    private record TokenResponse(string Token);
}
