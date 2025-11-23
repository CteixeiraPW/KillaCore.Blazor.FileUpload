using KillaCore.Blazor.FileUpload.Controllers;
using KillaCore.Blazor.FileUpload.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KillaCore.Blazor.FileUpload.Extension;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlazorFileUpload(this IServiceCollection services, string secretKey)
    {
        // 1. Validate Configuration
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentNullException(nameof(secretKey), "The Upload Secret Key cannot be null or empty.");

        if (secretKey.Length < 16)
            throw new ArgumentException("Key must be at least 16 chars.", nameof(secretKey));

        // 2. Register Security Service (Singleton is fine as it's stateless)
        services.AddSingleton<IFileUploadSecurityService>(new HmacFileUploadSecurityService(secretKey));

        // 3. Register Controller Discovery
        // We explicitly add the assembly containing 'UploadsController' to the MVC Application Parts.
        // This ensures the API endpoint works even if the developer forgets to scan referenced assemblies.
        services.AddControllers()
                .AddApplicationPart(typeof(UploadsController).Assembly);

        return services;
    }
}