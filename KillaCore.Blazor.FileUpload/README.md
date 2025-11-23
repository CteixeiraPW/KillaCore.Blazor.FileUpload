-----

# KillaCore.Blazor.FileUpload

A robust, secure, and high-performance file upload solution for Blazor applications. This library implements a **Producer-Consumer pipeline** to handle high-concurrency uploads while separating network-bound operations (uploading) from CPU-bound operations (hashing and saving).

## 🚀 Key Features

  * **Pipeline Architecture:** Decouples network uploads from file processing using `System.Threading.Channels`.
  * **Smart Concurrency:** Configurable limits for concurrent network uploads vs. concurrent CPU processors.
  * **Security:** HMAC SHA-256 signed tokens ensure that only valid sessions can utilize the upload API endpoints.
  * **Resiliency:** Handles cancellations, network interruptions, and automatic temp file cleanup.
  * **Lifecycle Management:** Tracks file states through `Pending` -\> `Uploading` -\> `Hashing` -\> `Verifying` -\> `Saving`.
  * **Detailed Progress:** Weighted progress bars calculation (e.g., 60% upload, 30% hashing, 10% saving).
  * **Duplicate Detection:** Built-in support for local batch duplicate checks and hooks for remote server duplicate verification.

-----

## 📦 Installation & Setup

### 1\. Register Services

In your `Program.cs`, register the file upload services. You must provide a secret key (min 16 characters) for the HMAC security service.

```csharp
// Program.cs
using KillaCore.Blazor.FileUpload.Extension;

var builder = WebApplication.CreateBuilder(args);

// Register Blazor File Upload
// The secret key is used to sign upload tokens securely.
string uploadKey = builder.Configuration["FileUpload:SecretKey"] 
                   ?? "Your-Super-Secure-Secret-Key-Here-Min-16-Chars";

builder.Services.AddBlazorFileUpload(uploadKey);

var app = builder.Build();
```

### 2\. Add the Controller

Ensure your backend is set up to map controllers. The `AddBlazorFileUpload` extension automatically registers the `UploadsController` assembly, but you must map endpoints.

```csharp
app.MapControllers();
```

### 3\. Static Assets (JS)

The library uses a JavaScript module for `XMLHttpRequest` handling (to support accurate upload progress). Ensure your `App.razor` or `_Layout.cshtml` includes the Blazor script. The component lazily loads the worker script `fileUploadWorker.js`, so no manual script tag is required in your HTML.

-----

## 💻 Usage

### 1\. The Component

Add the `FileUploadProcessor` to your Blazor page. You need a standard HTML `<input type="file" />` or a Blazor `InputFile`.

```razor
@page "/upload"
@using KillaCore.Blazor.FileUpload.Components
@using KillaCore.Blazor.FileUpload.Models

<h3>Secure File Uploader</h3>

<InputFile id="fileInput" OnChange="HandleFileSelection" multiple />

<button class="btn btn-primary" @onclick="StartUpload" disabled="@(!_filesSelected)">
    Upload Files
</button>

<button class="btn btn-danger" @onclick="CancelUpload">
    Cancel All
</button>

<FileUploadProcessor @ref="_uploader"
                     InputSelector="#fileInput"
                     Options="_options"
                     UserId="@CurrentUserId"
                     OnEvent="HandleUploadEvent"
                     OnFileUploadCompleted="SaveFileToDisk"
                     OnVerifyRemoteDuplicate="CheckRemoteDuplicate" />

@foreach (var transfer in _uploader?.Transfers ?? Enumerable.Empty<FileTransferData>())
{
    <div class="upload-item">
        <span>@transfer.FileName</span>
        <div class="progress">
            <div class="progress-bar" style="width: @(transfer.LifecyclePercent)%">
                @transfer.StatusMessage (@transfer.LifecyclePercent.ToString("F0")%)
            </div>
        </div>
    </div>
}

@code {
    private FileUploadProcessor? _uploader;
    private FileProcessingOptions _options = new();
    private bool _filesSelected = false;
    private string CurrentUserId = "User-123"; // Get from AuthenticationState

    private void HandleFileSelection(InputFileChangeEventArgs e)
    {
        _filesSelected = true;
    }

    private async Task StartUpload()
    {
        // Note: You must pass the IBrowserFile list to the processor
        // In a real scenario, capture 'e.GetMultipleFiles()' from the InputFile OnChange event
        // and pass it here.
        // await _uploader.ProcessInputFiles(capturedFiles); 
    }
    
    private async Task CancelUpload()
    {
        if (_uploader != null) await _uploader.CancelAllAsync();
    }

    private void HandleUploadEvent(FileNotificationEvent e)
    {
        // Force UI refresh on progress or status change
        StateHasChanged();
    }

    // Optional: Callback to save the file after validation/upload
    private async Task SaveFileToDisk(FileTransferData data, Stream stream)
    {
        var path = Path.Combine("C:\\Uploads", data.FileName);
        using var fs = new FileStream(path, FileMode.Create);
        await stream.CopyToAsync(fs);
    }

    // Optional: Callback to check if file exists on server
    private async Task<bool> CheckRemoteDuplicate(FileTransferData data)
    {
        // Check database or file system using data.DetectedHash
        await Task.Delay(100); // Simulation
        return false; 
    }
}
```

-----

## ⚙️ Configuration

You can customize the behavior using `FileProcessingOptions`.

```csharp
var options = new FileProcessingOptions
{
    // Endpoints
    UploadEndpointUrl = "api/uploads/temp",

    // Limits
    MaxFiles = 50,
    MaxSizeFileBytes = 1024 * 1024 * 100, // 100 MB
    
    // Concurrency Tuning
    MaxConcurrentUploads = 5,    // Network bound
    MaxConcurrentProcessors = 2, // CPU bound (Hashing/Saving)

    // UI Throttling
    UIProgressUpdateIntervalMs = 300,

    // Feature Toggles
    EnabledFeatures = new HashSet<FileUploadFeature> 
    { 
        FileUploadFeature.VerifyLocalDuplicates, // Hash check within batch
        FileUploadFeature.VerifyRemoteDuplicates, // Check against server
        FileUploadFeature.SaveToServer // Move from Temp -> Final
    },
    
    // Allowed Types
    AllowedMimeTypes = new List<string> { "image/png", "image/jpeg", "application/pdf" }
};
```

-----

## 🔐 Security Architecture

This library employs a **Signed Token Pattern** to secure the upload endpoint.

1.  **Token Generation:** When an upload begins, the Blazor Server component generates a JWT-like token containing `FileId`, `UserId`, `Expiration`, and a `Signature`.
2.  **HMAC Signing:** The signature is generated using `HMACSHA256` with the server-side secret key.
3.  **Client Handling:** The token is passed to the JavaScript worker.
4.  **API Validation:** The `UploadsController` validates the token before accepting any bytes. It checks:
      * Signature integrity.
      * Token expiration (default 5 mins).
      * User ownership (optional).

**Benefit:** Users cannot bypass the application logic or file size limits by hitting the API directly with tools like Postman, as they cannot generate a valid signature.

-----

## 🏗️ Architecture: Producer-Consumer

The `FileUploadProcessor` handles files in two distinct stages to optimize server resources:

1.  **Stage 1: Network Producer (Async Parallel)**

      * Uploads files to a temporary location via `UploadsController`.
      * Concurrency controlled by `MaxConcurrentUploads`.
      * Pushes successful uploads to a `Channel<ProcessingJob>`.

2.  **Stage 2: CPU Consumer (Async Parallel)**

      * Reads from the Channel.
      * Calculates SHA-256 Hash (CPU intensive).
      * Performs Duplicate Checks.
      * Executes the `OnFileUploadCompleted` callback to save the final file.
      * Concurrency controlled by `MaxConcurrentProcessors`.

-----

## 📋 API Reference

### Enums

| Enum | Description |
| :--- | :--- |
| `TransferStatus` | Pending, InProgress, Completed, Failed, Cancelled, Skipped. |
| `TransferStage` | Uploading, Hashing, VerifyingLocal, VerifyingRemote, ServerSaving. |
| `EventNotificationType` | Events for Batch (Started, Completed) and Files (Progress, Failed). |

### Callbacks

| Callback | Signature | Purpose |
| :--- | :--- | :--- |
| `OnEvent` | `EventCallback<FileNotificationEvent>` | Triggered on progress or status changes. Used to update UI. |
| `OnVerifyRemoteDuplicate` | `Func<FileTransferData, Task<bool>>` | Return `true` to reject the file as a duplicate. |
| `OnFileUploadCompleted` | `Func<FileTransferData, Stream, Task>` | The final step. The stream provided reads from the temp file. |

-----

## ⚠️ Requirements

  * .NET 6.0, 7.0, or 8.0+
  * Blazor Server or Blazor Web App (Interactive Server)
  * Note: This library relies on `Microsoft.AspNetCore.Mvc` for the controller implementation.
