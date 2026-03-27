using KillaCore.Blazor.FileUpload.Client.Extension;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddBlazorFileUploadClient();

await builder.Build().RunAsync();
