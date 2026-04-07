using FileSignatures;
using KillaCore.Blazor.FileUpload.Controllers;
using KillaCore.Blazor.FileUpload.Services;
using KillaCore.Blazor.FileUpload.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KillaCore.Blazor.FileUpload.Extension;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKillaCoreFileUploadServer(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = "KillaCoreFileUpload")
    {
        // 1. Bind the appsettings.json section to our strongly-typed class
        services.Configure<FileUploadServerOptions>(configuration.GetSection(configSectionName));

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
        services.AddMemoryCache();
        services.TryAddSingleton<IFileUploadSecurityService, HmacFileUploadSecurityService>();
        services.TryAddSingleton<IBatchDuplicateTracker, BatchDuplicateTracker>();

        // 4. The Janitor Service (Background Cleanup)
        services.AddHostedService<TempFileCleanupService>();

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