using FileSignatures;
using KillaCore.Blazor.FileUpload.Controllers;
using KillaCore.Blazor.FileUpload.Filters;
using KillaCore.Blazor.FileUpload.Models;
using KillaCore.Blazor.FileUpload.Services;
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
        bool userIdEnforcement = false,
        string configSectionName = FileUploadServerOptions.DefaultConfigSectionName,
        bool allowAnonymousUploads = false)
    {
        var section = configuration.GetSection(configSectionName);

        // 1. Bind the appsettings.json section to our strongly-typed class
        services.Configure<FileUploadServerOptions>(section);

        // The transport ceiling has to be read eagerly: FormOptions and Kestrel are configured
        // here, before any IOptions instance can be resolved.
        var bound = new FileUploadServerOptions();
        section.Bind(bound);

        // Whether the developer said anything at all about limits, so startup can warn when the
        // built-in defaults are silently in force.
        bool limitsConfigured =
            section.GetSection(nameof(FileUploadServerOptions.MaxFileBytes)).Exists() ||
            section.GetSection(nameof(FileUploadServerOptions.AllowedMimeTypes)).Exists() ||
            section.GetSection(nameof(FileUploadServerOptions.Profiles)).Exists();

        services.TryAddSingleton(new FileUploadConfigState(configSectionName, limitsConfigured));

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

        // Holds each upload between arrival and acceptance. Registered with TryAdd so an
        // application can supply its own — backed by S3, Azure Blob or a share — when local
        // scratch space is the constraint.
        services.TryAddSingleton<IUploadQuarantineStore, LocalDiskQuarantineStore>();

        // Rejects over-sized uploads before model binding buffers the body. Referenced by
        // [ServiceFilter] on the upload action, so it must be resolvable.
        services.TryAddSingleton<RequestSizeGuardFilter>();

        // 4. The Janitor Service (Background Cleanup) and the startup limit report
        services.AddHostedService<TempFileCleanupService>();
        services.AddHostedService<FileUploadConfigurationReporter>();

        // 5. Register Controller Discovery
        //    The upload endpoints inherit whatever authorization the host application has set up,
        //    including its FallbackPolicy. Pass allowAnonymousUploads: true to opt them out
        //    explicitly — see AllowAnonymousUploadsConvention for why this is not an attribute.
        services.AddControllers(mvcOptions =>
                {
                    if (allowAnonymousUploads)
                        mvcOptions.Conventions.Add(new AllowAnonymousUploadsConvention());
                })
                .AddApplicationPart(typeof(UploadsController).Assembly);

        // 6. Transport ceiling, from configuration rather than a hardcoded constant. This is what
        //    actually aborts an over-sized request mid-stream; the per-profile MaxFileBytes is a
        //    policy check on a request the server already agreed to read.
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = bound.MaxRequestBytes;
        });

        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = bound.MaxRequestBytes;
        });

        // 7. Optional: UserId enforcement filter for authenticated apps
        if (userIdEnforcement)
        {
            services.AddScoped<EnforceAuthenticatedUserFilter>();
        }

        return services;
    }
}
