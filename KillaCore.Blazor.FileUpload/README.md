-----

# KillaCore.Blazor.FileUpload

A robust, secure, and high-performance file upload solution for Blazor Server and Interactive Server applications. This library implements a **Producer-Consumer pipeline** to handle high-concurrency uploads while strictly separating network-bound operations (uploading) from CPU-bound operations (hashing, verification, and saving).

## 🚀 Key Features

  * **Pipeline Architecture:** Decouples network I/O from file processing using `System.Threading.Channels`. \* **Smart Concurrency:** Configurable independent limits for concurrent network uploads vs. concurrent CPU processors.
  * **End-to-End Security:**
      * **HMAC SHA-256 Signed Tokens:** Prevents unauthorized API access and replay attacks.
      * **Data Protection:** Encrypts file validation rules (MIME types) so they cannot be tampered with on the client.
      * **Magic Number Inspection:** Server-side validation of file content headers (not just file extensions).
  * **Resiliency:** Full support for `CancellationToken` propagation. Cancelling a batch immediately stops network requests, hashing operations, and database calls.
  * **Lifecycle Management:** Tracks file states through `Pending` $\rightarrow$ `Uploading` $\rightarrow$ `Hashing` $\rightarrow$ `Verifying` $\rightarrow$ `Saving`.
  * **Memory Efficient:** Implements strict disposal patterns to clean up `CancellationTokenSource` and temporary files automatically.

-----

## 📦 Installation & Setup

### 1\. Register Services

In your `Program.cs`, register the file upload services. You must provide a strong secret key (min 16 characters) for the HMAC security service.

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

### 2\. Map Controllers

Ensure your backend is set up to map controllers. The extension automatically registers the `UploadsController` assembly, but you must map the endpoints.

```csharp
app.MapControllers();
```

### 3\. IIS / Reverse Proxy Configuration

By default, ASP.NET Core limits request bodies to \~30MB. To allow larger uploads, you must configure the request limits in `web.config` (if using IIS) or Kestrel.

**web.config (IIS):**

```xml
<security>
  <requestFiltering>
    <requestLimits maxAllowedContentLength="524288000" />
  </requestFiltering>
</security>
```

-----

## 💻 Usage Example

Here is a complete example of a Blazor page that handles multiple file uploads, displays a progress bar, and saves the files to disk.

```razor
@page "/upload"
@using KillaCore.Blazor.FileUpload.Components
@using KillaCore.Blazor.FileUpload.Models
@using System.IO

<h3>Secure File Processor</h3>

<div class="mb-3">
    <InputFile id="myFileInput" OnChange="HandleInputOnChange" multiple class="form-control" />
</div>

<div class="mb-3">
    <button class="btn btn-danger" @onclick="CancelAll" disabled="@(!_isUploading)">
        Cancel All
    </button>
</div>

@foreach (var file in _processor?.Transfers ?? Enumerable.Empty<FileTransferData>())
{
    <div class="card mb-2">
        <div class="card-body p-2">
            <div class="d-flex justify-content-between">
                <span>@file.FileName (@(file.FileSize / 1024) KB)</span>
                <span>@file.Status - @file.StageProgressPercent.ToString("F0")%</span>
            </div>
            
            <div class="progress" style="height: 5px;">
                <div class="progress-bar" role="progressbar" 
                     style="width: @(file.LifecyclePercent)%"></div>
            </div>
            
            @if (file.Status == TransferStatus.Failed)
            {
                <small class="text-danger">Error: @file.StatusMessage</small>
            }
        </div>
    </div>
}

<FileUploadProcessor @ref="_processor"
                     InputSelector="#myFileInput"
                     Options="_options"
                     UserId="@CurrentUserId"
                     OnEvent="HandleUploadEvent"
                     OnFileServerUpload="SaveFileToDisk"
                     OnVerifyRemoteDuplicate="CheckRemoteDuplicate" 
                     OnFilesUploadCompleted="HandleBatchCompleted" />

@code {
    private FileUploadProcessor? _processor;
    private bool _isUploading => _processor?.Transfers.Any(t => !t.IsFinished) ?? false;
    private string CurrentUserId = "User-123"; // Retrieve from auth state

    // Configure specific limits and features
    private FileProcessingOptions _options = new()
    {
        MaxFiles = 10,
        MaxSizeFileBytes = 50 * 1024 * 1024, // 50 MB
        MaxConcurrentUploads = 3,            // Network throttle
        MaxConcurrentProcessors = 2,         // CPU throttle
        EnabledFeatures = new HashSet<FileUploadFeature> 
        { 
            FileUploadFeature.VerifyRemoteDuplicates, 
            FileUploadFeature.SaveToServer 
        }
    };

    private async Task HandleInputOnChange(InputFileChangeEventArgs e)
    {
        // Pass the files to the processor to begin the pipeline
        var files = e.GetMultipleFiles(_options.MaxFiles);
        await _processor!.ProcessInputFiles(files);
    }

    private async Task CancelAll()
    {
        await _processor!.CancelAllAsync();
    }

    // Callback: Update UI when progress changes
    private void HandleUploadEvent(FileNotificationEvent e)
    {
        StateHasChanged();
    }

    // Callback: Check if file exists (Optional)
    // Note: Accepts CancellationToken to support immediate stopping
    private async Task<bool> CheckRemoteDuplicate(FileTransferData data, CancellationToken ct)
    {
        // Example: Check your DB using data.DetectedHash
        await Task.Delay(100, ct); 
        return false; // Return true to skip upload
    }

    // Callback: Save the file (Optional)
    // The stream provides read access to the temp file on the server
    private async Task SaveFileToDisk(FileTransferData data, Stream stream, CancellationToken ct)
    {
        var path = Path.Combine("C:\\Uploads", data.FileName);
        
        // Use the token to ensure we stop writing if the user cancels
        using var fs = new FileStream(path, FileMode.Create);
        await stream.CopyToAsync(fs, ct);
    }

    // Callback: Fires when the entire batch is finished
    private Task HandleBatchCompleted((IReadOnlyList<FileTransferData> Files, string BatchId) result)
    {
        Console.WriteLine($"Batch {result.BatchId} finished. Processed {result.Files.Count} files.");
        return Task.CompletedTask;
    }
}
```

-----

## ⚙️ Configuration (`FileProcessingOptions`)

| Property | Default | Description |
| :--- | :--- | :--- |
| `UploadEndpointUrl` | `api/uploads/temp` | The controller route for the initial raw upload. |
| `MaxFiles` | `10` | Maximum number of files allowed in a single batch. |
| `MaxSizeFileBytes` | `50 MB` | Hard limit per file. Checked on Client and Server. |
| `AllowedMimeTypes` | Common Images/Docs | List of allowed MIME types. Sent as an **encrypted** policy header. |
| `MaxConcurrentUploads` | `5` | How many files to upload over the network simultaneously. |
| `MaxConcurrentProcessors`| `2` | How many files to hash/save simultaneously (CPU bound). |
| `EnabledFeatures` | All | Toggle `VerifyLocalDuplicates`, `VerifyRemoteDuplicates`, or `SaveToServer`. |

-----

## 🔐 Security Architecture

This library employs a **Signed Token Pattern** to secure the upload endpoint.

1.  **Token Generation:** When an upload begins, the Blazor Server component generates a token containing `FileId`, `UserId`, `Expiration`, and a `HMACSHA256` signature.
2.  **Encrypted Policy:** The allowed MIME types are encrypted server-side using `IDataProtectionProvider` and sent to the client. The client echoes this back to the API, ensuring the user cannot tamper with allowed file types in JavaScript.
3.  **API Validation:** The `UploadsController` validates the token signature and decrypts the policy before accepting any bytes.
4.  **Content Inspection:** The server inspects the file's "Magic Numbers" (file signature) to verify the actual content matches the extension.

-----

## 🏗️ Architecture

The `FileUploadProcessor` handles files in two distinct stages to optimize server resources:

1.  **Stage 1: Network Producer**

      * Uploads files to a temporary location via `UploadsController`.
      * Concurrency is limited by `MaxConcurrentUploads` to prevent saturating the bandwidth.
      * Pushes successful upload tokens to a `Channel<ProcessingJob>`.

2.  **Stage 2: CPU Consumer**

      * Reads from the Channel.
      * Claims the temporary file from the `FileUploadBridgeService`.
      * Calculates SHA-256 Hash (CPU intensive).
      * Executes the `OnFileUploadCompleted` callback.
      * Concurrency is limited by `MaxConcurrentProcessors` to prevent thread starvation.

## 📄 License

MIT License. See [LICENSE.txt](https://www.google.com/search?q=LICENSE.txt) for more information.