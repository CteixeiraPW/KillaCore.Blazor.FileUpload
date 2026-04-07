# KillaCore.Blazor.FileUpload

A high-performance, secure, and modern file upload pipeline designed for Blazor Server, Blazor WebAssembly, and Blazor WebApp (Auto) applications. 

This library solves the fundamental issue of Blazor SignalR message size limits by offloading the actual data transfer to a dedicated Javascript worker (`XMLHttpRequest`) and Web API pipeline. It strictly separates the UI state machine from server-side CPU-bound operations (hashing, duplicate verification, and disk saving), ensuring your Blazor UI remains highly responsive regardless of file size.

## 🚀 Key Features

* **Dual-Package Architecture:** Seamlessly supports .NET 10 Blazor WebApp Auto rendering modes by strictly separating Client UI logic from Server API logic.
* **SignalR Bypass:** Transfers files directly to an API controller, completely preventing Blazor Server circuit disconnects and memory spikes.
* **Multi-Tier Duplicate Detection:** Saves bandwidth and server resources by intelligently skipping identical files within the same batch using instant client-side heuristics and server-side SHA-256 cryptographic hashing.
* **End-to-End Security:**
  * **Anti-Replay Tokens:** Uses short-lived, one-time-use secure tokens to prevent unauthorized API access.
  * **Encrypted Policies:** Uses `IDataProtection` to encrypt validation rules (like Allowed MIME types) so they cannot be tampered with on the client.
  * **Magic Number Inspection:** Validates actual file content headers on the server, ensuring users cannot bypass security by renaming file extensions.
* **Extensible Server Hooks:** A clean `IFileUploadServerHooks` interface allows you to execute custom logic (saving to DB, AWS S3, Azure Blob, or local disk) without altering the core pipeline.
* **Resiliency & Cancellation:** Full support for `CancellationToken` propagation. Cancelling a batch in the UI immediately halts network requests and server-side saving operations.
* **Orphan Cleanup Janitor:** Includes a lightweight, zero-impact BackgroundService that automatically sweeps the temporary storage folder to prevent disk exhaustion in the event of hard server crashes or IIS App Pool recycles.
* **Extensible Server Hooks:** A clean `IFileUploadServerHooks` interface allows you to execute custom logic (saving to DB, AWS S3, Azure Blob, or local disk) without altering the core pipeline.
  * **Dynamic Hook Routing:** Leverages modern .NET Keyed Dependency Injection, allowing you to register multiple unique upload behaviors (e.g., "ProfilePictures" vs "Invoices") in the same app and route them effortlessly from the UI.
---

## 📦 Installation & Setup

Because this solution utilizes a secure Client-to-Server architecture, you must install the appropriate packages based on your Blazor hosting model.

### 1. Install the NuGet Packages

**For the Server Project (API, Validation, Disk I/O):**
```bash
dotnet add package KillaCore.Blazor.FileUpload
```

**For the Client Project (UI Components, WASM, JS Interop):**
```bash
dotnet add package KillaCore.Blazor.FileUpload.Client
```

---

### 2. Register Services (`Program.cs`)

Your setup depends on your Blazor hosting model. 

#### Option A: Blazor WebApp (Auto Mode) OR Blazor WebAssembly (Hosted)
* **Client Project `Program.cs`:** Register the client services so the UI can communicate with your API.
    ```csharp
    builder.Services.AddKillaCoreFileUploadClient();
    ```
* **Server Project `Program.cs`:** Register the server-side validation, caching, and API services. Also register the client services here if you are utilizing Server Prerendering.
    ```csharp
    // 1. Required for the UploadsController API
    builder.Services.AddControllers();
    
    // 2. Register KillaCore Packages
    builder.Services.AddKillaCoreFileUploadServer(builder.Configuration);;
    builder.Services.AddKillaCoreFileUploadClient(); // If prerendering Client components
    
    // 3. Register your custom server hooks using Keyed DI!
    builder.Services.AddScoped<IFileUploadServerHooks, ApplicationUploadHooks>();
    
    // The string key tells the server which logic to execute based on the UI's context.
    // Example: You can register as many different hooks as your app needs!
    // builder.Services.AddKeyedScoped<IFileUploadServerHooks, ApplicationUploadHooks>("Default");
    // builder.Services.AddKeyedScoped<IFileUploadServerHooks, ProfilePictureHooks>("ProfileImages");
    // builder.Services.AddKeyedScoped<IFileUploadServerHooks, InvoiceHooks>("Invoices");
    ```

#### Option B: Blazor Server (Interactive Server Only)
Since everything runs on one machine, register everything in your single `Program.cs`:
```csharp
builder.Services.AddControllers();

builder.Services.AddKillaCoreFileUploadServer(builder.Configuration);;
builder.Services.AddKillaCoreFileUploadClient();

builder.Services.AddScoped<IFileUploadServerHooks, ApplicationUploadHooks>();
```

### 3. Map the Controllers (Server Project)
Ensure your backend is set up to route requests to the package's internal `UploadsController`.
```csharp
app.MapControllers(); // Ensure this is before app.Run()
```

### 4. Configure `appsettings.json` (Server Project)
You must define a secure key for generating Anti-Replay tokens, and optionally specify where temporary files should be stored during the upload process.

```json
{
  "KillaCoreFileUpload": {
    "SecretKey": "Your-Super-Secure-Secret-Key-Min-16-Chars",
    "TempFolder": "KillaCoreUploads" 
  }
}
```
*Note: If `TempFolder` is just a name, it will be placed in the OS temp directory. You can also provide an absolute path like `D:\TempUploads`.*

---

### 4. Implement Server Hooks (Your Custom Logic)

The package handles the networking, validation, and temporary file creation automatically. However, **you** must tell the application what to do with the files once they safely reach your server.

In your **Server Project**, create a class that implements `IFileUploadServerHooks`:

```csharp
using KillaCore.Blazor.FileUpload.Services;
using KillaCore.Blazor.FileUpload.Client.Models;

public class ApplicationUploadHooks : IFileUploadServerHooks
{
    // 1. Check if the file already exists (Optional)
    public Task<bool> CheckRemoteDuplicateAsync(string detectedHash, CancellationToken ct)
    {
        // Example: bool exists = _dbContext.Files.Any(f => f.Hash == detectedHash);
        return Task.FromResult(false); 
    }

    // 2. Save the file to your permanent storage
    public async Task SaveFileAsync(FileTransferData data, Stream fileStream, CancellationToken ct)
    {
        string safeName = $"{Guid.NewGuid():N}{Path.GetExtension(data.FileName)}";
        string path = Path.Combine("C:\\SecureUploads", safeName);

        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        
        // Pass the CancellationToken to support immediate UI cancellation!
        await fileStream.CopyToAsync(fs, ct);

        // Map your database ID back to the model so the Blazor UI Client knows about it
        data.FinalResourceId = safeName; 
    }

    // 3. Fire logic when the whole batch finishes (Emails, Database updates, etc.)
    public Task OnBatchCompletedAsync(string batchId, IReadOnlyList<FileTransferData> files)
    {
        int successCount = files.Count(f => f.Status == TransferStatus.Completed);
        Console.WriteLine($"Batch {batchId} finished. {successCount} files saved successfully.");
        return Task.CompletedTask;
    }
}
```

---

## 💻 Usage Example (The UI Component)

You can now use the `FileUploadProcessor` in your **Client Project** (`.razor` files). The component operates in a "headless" state-machine style, giving you full control over how you render the progress bars.

```razor
@page "/upload"
@using KillaCore.Blazor.FileUpload.Client.Components
@using KillaCore.Blazor.FileUpload.Client.Models

<h3>Secure File Processor</h3>

<div class="mb-3">
    <InputFile id="myFileInput" OnChange="HandleInputOnChange" multiple class="form-control" />
</div>

@if (_processor?.Transfers.Any() == true)
{
    <ul class="list-group mb-3">
        @foreach (var file in _processor.Transfers)
        {
            <li class="list-group-item">
                <div class="d-flex justify-content-between">
                    <strong>@file.FileName</strong>
                    <span class="badge bg-secondary">@file.Status.ToString()</span>
                </div>
                
                <div class="progress mt-2" style="height: 20px;">
                    <div class="progress-bar @(file.IsFinished ? "bg-success" : "progress-bar-animated progress-bar-striped")" 
                         style="width: @(file.LifecyclePercent)%;">
                        @file.LifecyclePercent.ToString("0")%
                    </div>
                </div>
                <small class="text-muted">@file.StatusMessage</small>
            </li>
        }
    </ul>
    
    @if (!_processor.Transfers.All(x => x.IsFinished))
    {
        <button class="btn btn-danger" @onclick="() => _processor.CancelAllAsync()">Cancel Batch</button>
    }
    else
    {
        <button class="btn btn-secondary" @onclick="() => _processor.Clear()">Clear List</button>
    }
}

<FileUploadProcessor @ref="_processor"
                     InputSelector="#myFileInput"
                     Options="_options"
                     UserId="@CurrentUserId"
                     OnEvent="HandleUploadEvent" />

@code {
    private FileUploadProcessor _processor = default!;
    private string CurrentUserId = "User-123"; // Retrieve from auth state

    // Configure specific limits and features
    private readonly FileProcessingOptions _options = new()
    {
        MaxFiles = 5,
        MaxSizeFileBytes = 1024 * 1024 * 500, // 500 MB limit
        MaxConcurrentUploads = 2,             // Throttle network connections
        AllowedMimeTypes = ["image/jpeg", "image/png", "application/pdf"],
        EnabledFeatures = [
            FileUploadFeature.VerifyLocalDuplicates, 
            FileUploadFeature.VerifyRemoteDuplicates, 
            FileUploadFeature.SaveToServer
        ],
        UploadContext = "Default" // Must match the string used in AddKeyedScoped!
    };

    private async Task HandleInputOnChange(InputFileChangeEventArgs e)
    {
        // Pass the files to the processor to begin the pipeline
        var files = e.GetMultipleFiles(_options.MaxFiles).ToList();
        if (_processor != null)
        {
            await _processor.ProcessInputFiles(files);
        }
    }

    // Callback: Update UI when the Javascript worker reports progress
    private void HandleUploadEvent(FileNotificationEvent notification)
    {
        StateHasChanged();
    }
}
```

---

## ⚙️ Configuration (`FileProcessingOptions`)

| Property | Default | Description |
| :--- | :--- | :--- |
| `UploadContext` | `"Default"` | The string key used by the API to resolve the specific `IFileUploadServerHooks` implementation via Keyed DI. |
| `MaxFiles` | `10` | Maximum number of files allowed in a single batch selection. |
| `MaxSizeFileBytes` | `50 MB` | Hard size limit per file. Validated instantly on the client. |
| `AllowedMimeTypes` | `*` (Any) | List of allowed MIME types. Sent securely as an encrypted policy. |
| `MaxConcurrentUploads` | `5` | Throttle limit for how many parallel XHR network requests are executed. |
| `EnabledFeatures` | All | Toggle `VerifyLocalDuplicates`, `VerifyRemoteDuplicates`, or `SaveToServer` flags. |

---

## ⚠️ Important Production Considerations

### IIS & Web Server Request Limits
By default, ASP.NET Kestrel and IIS block HTTP POST requests larger than **30MB**. Even though this component supports 500MB+ configurations, your web server will reject the request with a `413 Payload Too Large` error before it ever reaches the `UploadsController`.
* **IIS (`web.config`):** You must increase the `maxAllowedContentLength`.

```xml
<security>
  <requestFiltering>
    <requestLimits maxAllowedContentLength="524288000" />
  </requestFiltering>
</security>
```
* **Kestrel (`Program.cs`):** You must configure `MaxRequestBodySize` on the Kestrel server options.

### Web Farms & Docker Swarm
This package relies on `IDataProtectionProvider` to encrypt upload policies. If you deploy your API across multiple servers or load balancers, you **must** configure a centralized Data Protection key ring (e.g., storing keys in Redis, Azure Blob Storage, or a shared network folder). If Server A encrypts the policy and the Load Balancer routes the file transfer to Server B, the upload will fail with a `CryptographicException` if Server B does not share the same decryption key.

---
## 📄 License
MIT License.