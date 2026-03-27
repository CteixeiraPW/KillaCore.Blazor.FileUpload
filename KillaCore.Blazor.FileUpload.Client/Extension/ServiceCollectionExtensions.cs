using KillaCore.Blazor.FileUpload.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KillaCore.Blazor.FileUpload.Client.Extension;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the File Upload Client services and configures the HttpClient needed to communicate with the Server API.
    /// </summary>
    public static IServiceCollection AddBlazorFileUploadClient(
        this IServiceCollection services,
        Action<HttpClient>? configureClient = null)
    {
        var builder = services.AddHttpClient<IFileUploadClientService, DefaultFileUploadClientService>();

        // Only apply if the developer passed custom configurations
        if (configureClient != null)
        {
            builder.ConfigureHttpClient(configureClient);
        }

        return services;
    }
}