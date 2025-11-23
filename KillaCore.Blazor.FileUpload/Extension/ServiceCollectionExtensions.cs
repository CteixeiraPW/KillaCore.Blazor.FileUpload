using FileSignatures;
using KillaCore.Blazor.FileUpload.Controllers;
using KillaCore.Blazor.FileUpload.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KillaCore.Blazor.FileUpload.Extension;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlazorFileUpload(this IServiceCollection services, string secretKey)
    {
        // 1. Validate Configuration
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentNullException(nameof(secretKey), "The Upload Secret Key cannot be null or empty.");

        if (secretKey.Length < 16)
            throw new ArgumentException("Key must be at least 16 chars (HMAC Requirement).", nameof(secretKey));

        // 2. Register FileSignatures (The "Universal Inspector")
        // We use Reflection to find every class in the library that inherits from 'FileFormat'.
        // This automatically registers Word, Excel, PDF, Images, etc.
        var allFormats = typeof(FileFormat)
            .Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(FileFormat)))
            .Select(static t => Activator.CreateInstance(t))
            .Where(t=> t is not null)
            .Cast<FileFormat>();

        // Register Inspector as Singleton (Thread-safe, High Performance)
        services.TryAddSingleton<IFileFormatInspector>(new FileFormatInspector(allFormats));

        // 3. Register Core Services
        // Security Service (Stateless logic for Token Validation)
        services.TryAddSingleton<IFileUploadSecurityService>(new HmacFileUploadSecurityService(secretKey));

        // Bridge Service (Stateful logic for Temp File Holding & Anti-Replay)
        services.TryAddSingleton<IFileUploadBridgeService, FileUploadBridgeService>();

        // 4. Register Controller Discovery
        // Ensures the API endpoint is reachable even if the user doesn't scan this assembly.
        services.AddControllers()
                .AddApplicationPart(typeof(UploadsController).Assembly);

        // 5. Configure Form Options (Allows the Controller to read large Multipart bodies)
        services.Configure<FormOptions>(options =>
        {
            // Set to 500MB (or whatever your library supports)
            options.MultipartBodyLengthLimit = 524_288_000;
        });

        // 6. Configure Kestrel (For when running without IIS)
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = 524_288_000; // 500MB
        });

        return services;
    }
}