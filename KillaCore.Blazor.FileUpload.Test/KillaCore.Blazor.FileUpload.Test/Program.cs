using KillaCore.Blazor.FileUpload.Client.Extension;
using KillaCore.Blazor.FileUpload.Extension;
using KillaCore.Blazor.FileUpload.Services;
using KillaCore.Blazor.FileUpload.Test.Components;
using KillaCore.Blazor.FileUpload.Test.Services;

var builder = WebApplication.CreateBuilder(args);


// 1. Register the backend API & security services (from your Server package)
builder.Services.AddKillaCoreFileUploadServer(builder.Configuration);

// 2. Register the DEFAULT hook (Fallback if no X-Upload-Context header is sent)
builder.Services.AddKeyedScoped<IFileUploadServerHooks, ApplicationUploadHooks>("TestUpload");

// 3. Register the frontend UI services (from your Client package)
// When the component renders on the server via SignalR, it needs an absolute URL to call its own API.
builder.Services.AddBlazorFileUploadClient();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(KillaCore.Blazor.FileUpload.Test.Client._Imports).Assembly);

app.Run();
