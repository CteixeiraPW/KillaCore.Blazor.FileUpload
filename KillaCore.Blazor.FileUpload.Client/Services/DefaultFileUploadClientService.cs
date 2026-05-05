using KillaCore.Blazor.FileUpload.Client.Models;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;

namespace KillaCore.Blazor.FileUpload.Client.Services;

public class DefaultFileUploadClientService : IFileUploadClientService
{
    private readonly HttpClient _httpClient;
    private static readonly string _prefix = FileUploadConstants.API_ROUTE_PREFIX;

    public DefaultFileUploadClientService(HttpClient httpClient, NavigationManager navManager)
    {
        _httpClient = httpClient;

        // Set BaseAddress here — this runs per-scope so NavigationManager is available
        _httpClient.BaseAddress ??= new Uri(navManager.BaseUri);
    }

    public async Task<string> GetPolicyTokenAsync(FileProcessingOptions options, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync($"{_prefix}/policy", options.AllowedMimeTypes, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        return result?.Token ?? string.Empty;
    }

    public async Task<string> GetUploadTokenAsync(string fileId, string userId, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(
            $"{_prefix}/token/{Uri.EscapeDataString(fileId)}?userId={Uri.EscapeDataString(userId)}", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        return result?.Token ?? string.Empty;
    }

    public async Task NotifyBatchCompletedAsync(string batchId, IEnumerable<FileTransferData> files, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"{_prefix}/batch/{batchId}/complete", files, ct);
        response.EnsureSuccessStatusCode();
    }

    private record TokenResponse(string Token);
}