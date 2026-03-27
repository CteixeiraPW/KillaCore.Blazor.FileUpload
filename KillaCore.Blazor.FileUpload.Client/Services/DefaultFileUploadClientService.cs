using KillaCore.Blazor.FileUpload.Client.Models;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;

namespace KillaCore.Blazor.FileUpload.Client.Services;

public class DefaultFileUploadClientService : IFileUploadClientService
{
    private readonly HttpClient _httpClient;

    // Inject both HttpClient AND NavigationManager
    public DefaultFileUploadClientService(HttpClient httpClient, NavigationManager navManager)
    {
        _httpClient = httpClient;

        // Automatically set the absolute URL for Blazor Server 
        // (WASM usually sets this automatically, so we check for null first)
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(navManager.BaseUri);
        }
    }

    public async Task<string> GetPolicyTokenAsync(FileProcessingOptions options, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync("api/uploads/policy", options.AllowedMimeTypes, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        return result?.Token ?? string.Empty;
    }

    public async Task<string> GetUploadTokenAsync(string fileId, string userId, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync($"api/uploads/token/{fileId}", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        return result?.Token ?? string.Empty;
    }

    public async Task NotifyBatchCompletedAsync(string batchId, IEnumerable<FileTransferData> files, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/uploads/batch/{batchId}/complete", files, ct);
        response.EnsureSuccessStatusCode();
    }

    private record TokenResponse(string Token);
}