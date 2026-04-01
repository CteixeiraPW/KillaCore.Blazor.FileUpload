using FileSignatures;
using KillaCore.Blazor.FileUpload.Controllers;
using KillaCore.Blazor.FileUpload.Services;
using KillaCore.Blazor.FileUpload.Models; // ADDED: For FileUploadServerOptions
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration; // ADDED: For IConfiguration
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KillaCore.Blazor.FileUpload.Extension;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlazorFileUpload<THooks>(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = "KillaCoreFileUpload")
        where THooks : class, IFileUploadServerHooks
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

        // UPDATED: Let the DI container build this so it can inject IOptions<FileUploadServerOptions>
        services.TryAddSingleton<IFileUploadSecurityService, HmacFileUploadSecurityService>();
        services.TryAddSingleton<IFileUploadBridgeService, FileUploadBridgeService>();

        // 4. The Janitor Service (Background Cleanup)
        // This tells ASP.NET Core to boot up the Janitor quietly in the background
        services.AddHostedService<TempFileCleanupService>();

        // 5. Register the Developer's Hooks
        // We use Scoped here because the developer might need to inject things like a DbContext 
        // into their hooks to save file metadata to a database.
        services.AddScoped<IFileUploadServerHooks, THooks>();

        // 6. Register Controller Discovery
        services.AddControllers()
                .AddApplicationPart(typeof(UploadsController).Assembly);

        // 7. Configure Form Options
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 524_288_000;
        });

        // 8. Configure Kestrel
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = 524_288_000;
        });

        return services;
    }
}