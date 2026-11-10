using KillaCore.Blazor.FileUpload.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KillaCore.Blazor.FileUpload.Client.Extension;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the File Upload Client services with resilience.
    /// BaseAddress is automatically resolved from NavigationManager.
    /// Optionally pass configureClient to override (e.g., custom base URL for cross-origin).
    /// </summary>
    /// <returns>
    /// The <see cref="IHttpClientBuilder"/> for the typed client, so the host application can
    /// attach its own authentication to the policy, token and batch-completion calls:
    /// <code>
    /// builder.Services.AddKillaCoreFileUploadClient()
    ///                 .AddHttpMessageHandler&lt;AuthorizationMessageHandler&gt;();
    /// </code>
    /// Note that this covers the JSON calls only. The file transfer itself is an XHR driven by
    /// the component, so it authenticates through <c>GetUploadHeadersAsync</c> instead.
    /// </returns>
    public static IHttpClientBuilder AddKillaCoreFileUploadClient(
        this IServiceCollection services,
        Action<HttpClient>? configureClient = null)
    {
        var builder = services.AddHttpClient<IFileUploadClientService, DefaultFileUploadClientService>();

        if (configureClient != null)
        {
            builder.ConfigureHttpClient(configureClient);
        }

        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
        });

        return builder;
    }
}
