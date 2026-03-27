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
    public static IServiceCollection AddBlazorFileUpload<THooks>(this IServiceCollection services, string secretKey)
        where THooks : class, IFileUploadServerHooks
    {
        // 1. Validate Configuration
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentNullException(nameof(secretKey), "The Upload Secret Key cannot be null or empty.");

        if (secretKey.Length < 16)
            throw new ArgumentException("Key must be at least 16 chars (HMAC Requirement).", nameof(secretKey));

        // 2. Register FileSignatures (The "Universal Inspector")
        var allFormats = typeof(FileFormat)
            .Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(FileFormat)))
            .Select(static t => Activator.CreateInstance(t))
            .Where(t => t is not null)
            .Cast<FileFormat>();

        services.TryAddSingleton<IFileFormatInspector>(new FileFormatInspector(allFormats));

        // 3. Register Core Services
        services.TryAddSingleton<IFileUploadSecurityService>(new HmacFileUploadSecurityService(secretKey));
        services.TryAddSingleton<IFileUploadBridgeService, FileUploadBridgeService>();

        // 4. Register the Developer's Hooks (THE MISSING PIECE)
        // We use Scoped here because the developer might need to inject things like a DbContext 
        // into their hooks to save file metadata to a database.
        services.AddScoped<IFileUploadServerHooks, THooks>();

        // 5. Register Controller Discovery
        services.AddControllers()
                .AddApplicationPart(typeof(UploadsController).Assembly);

        // 6. Configure Form Options
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 524_288_000;
        });

        // 7. Configure Kestrel
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = 524_288_000;
        });

        return services;
    }
}