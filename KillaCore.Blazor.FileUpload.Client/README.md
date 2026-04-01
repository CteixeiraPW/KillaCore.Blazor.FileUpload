# KillaCore.Blazor.FileUpload.Client

This is the **client-side companion package** for the `KillaCore.Blazor.FileUpload` pipeline. It contains the headless Blazor UI components, JavaScript Web Workers, and client services required to run high-performance file uploads in Blazor WebAssembly and Blazor WebApp (Auto) projects.

⚠️ **IMPORTANT:** This package cannot function on its own. It is designed to safely bypass Blazor SignalR limits by streaming files directly to your backend Web API. You **must** install the primary `KillaCore.Blazor.FileUpload` package in your Server/API project to handle the cryptographic hashing, security tokens, and disk I/O.

---

## 🚀 Features included in this Client Package

* **SignalR Bypass:** Uses optimized JavaScript (`XMLHttpRequest`) to stream large files (up to 500MB+) directly to the server, keeping your Blazor UI thread perfectly responsive.
* **Instant Validation:** Validates file sizes and MIME types instantly in the browser before using any network bandwidth.
* **Smart Heuristics:** Instantly detects and prevents duplicate file uploads within the same batch to save bandwidth.
* **Headless UI Component:** Provides a `FileUploadProcessor` component that manages the entire complex state machine (`Pending` -> `Uploading` -> `Completed`) while giving you 100% control over the HTML/CSS of your progress bars.

---

## 📦 Installation

Install this package into your **Blazor WebAssembly** project, or the **Client project** of your Blazor WebApp.

```bash
dotnet add package KillaCore.Blazor.FileUpload.Client
```

*(Remember to install `KillaCore.Blazor.FileUpload` in your Server project!)*

---

## ⚙️ Setup

In your Client project's `Program.cs`, register the client-side services:

```csharp
using KillaCore.Blazor.FileUpload.Client.Extension;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Register the KillaCore File Upload Client Services
builder.Services.AddKillaCoreFileUploadClient();

await builder.Build().RunAsync();
```

---

## 💻 Basic Usage

Add the `FileUploadProcessor` to your Razor component. You provide a standard HTML `<InputFile>`, and the processor handles the rest!

```razor
@using KillaCore.Blazor.FileUpload.Client.Components
@using KillaCore.Blazor.FileUpload.Client.Models

<InputFile id="my-file-input" OnChange="HandleFileSelection" multiple />

<FileUploadProcessor @ref="_processor"
                     InputSelector="#my-file-input"
                     Options="_options"
                     OnEvent="HandleUploadEvent" />

@if (_processor?.Transfers.Any() == true)
{
    foreach (var file in _processor.Transfers)
    {
        <div>
            @file.FileName - @file.Status 
            (Progress: @file.LifecyclePercent.ToString("0")%)
        </div>
    }
}

@code {
    private FileUploadProcessor _processor = default!;

    // Configure your limits and features
    private readonly FileProcessingOptions _options = new()
    {
        MaxFiles = 5,
        MaxSizeFileBytes = 1024 * 1024 * 500, // 500 MB limit
        AllowedMimeTypes = ["image/jpeg", "image/png", "application/pdf"]
    };

    private async Task HandleFileSelection(InputFileChangeEventArgs e)
    {
        // Hand the files over to the processor
        var files = e.GetMultipleFiles(_options.MaxFiles).ToList();
        await _processor.ProcessInputFiles(files);
    }

    private void HandleUploadEvent(FileNotificationEvent notification)
    {
        // Force Blazor to re-render the UI when progress updates
        StateHasChanged();
    }
}
```

---

## 📖 Full Documentation

For the complete setup guide, including how to configure the Server API, handle database saves via `IFileUploadServerHooks`, and manage IIS limits, please visit the main repository:

**[GitHub - KillaCore.Blazor.FileUpload](https://github.com/CteixeiraPW/KillaCore.Blazor.FileUpload)**

## 📄 License
MIT License.