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
    // 1. Check if this content is already stored (Optional)
    public Task<bool> CheckRemoteDuplicateAsync(FileTransferData data, CancellationToken ct)
    {
        // The hash the server computed is already on the model
        var existing = _dbContext.Files.FirstOrDefault(f => f.Hash == data.DetectedHash);

        if (existing is null) return Task.FromResult(false);

        // Describe the copy that already exists so the UI can link the user to it.
        // SaveFileAsync is skipped and the file is reported as Skipped, NOT as a failure.
        data.FinalResourceId = existing.Id.ToString();

        return Task.FromResult(true);
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
    //    EVERY transfer in the batch arrives here - successes, failures and skips alike -
    //    so filter on Status. A batch in which every file failed is reported too.
    public Task OnBatchCompletedAsync(string batchId, IReadOnlyList<FileTransferData> files)
    {
        int successCount = files.Count(f => f.Status == TransferStatus.Completed);
        int skippedCount = files.Count(f => f.Status == TransferStatus.Skipped);
        int failedCount  = files.Count(f => f.Status == TransferStatus.Failed);

        Console.WriteLine($"Batch {batchId}: {successCount} saved, {skippedCount} skipped, {failedCount} failed.");
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
        UploadContext = "Default" // Must match the string used in AddKeyedScoped!
    };

    private async Task HandleInputOnChange(InputFileChangeEventArgs e)
    {
        // Pass int.MaxValue, NOT MaxFiles. GetMultipleFiles THROWS when the user selects more
        // files than its cap, and an unhandled throw here tears down an Interactive Server
        // circuit. Let the processor enforce MaxFiles instead: it marks the extras as Skipped
        // and raises FileSkipped, so the user is told why. Only file references are
        // materialised here, so a high ceiling costs nothing.
        var files = e.GetMultipleFiles(int.MaxValue).ToList();
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
| `MaxFiles` | `10` | Maximum number of files **accepted** per batch. Extras are marked `Skipped` with a `FileSkipped` event rather than rejected outright, so pass `int.MaxValue` to `GetMultipleFiles` and let the component enforce this limit. |
| `MaxSizeFileBytes` | `50 MB` | Hard size limit per file. Validated instantly on the client. |
| `AllowedMimeTypes` | images, PDF, Office formats | Types the client accepts, also sent to the server as the upload policy. An empty list means any type. `text/html` is **not** included by default — see *Serving uploaded content* below. |
| `MaxConcurrentUploads` | `5` | Throttle limit for how many parallel XHR network requests are executed. |
| `VerifyLocalDuplicates` | `true` | Skip a file when an earlier file in the same selection has the same name and size. A client-side convenience only — the server rejects duplicate **content** by SHA-256 either way. |

---

## ⚠️ Important Production Considerations

### Authorization

The upload endpoints carry **no `[AllowAnonymous]`**. They inherit whatever authorization your application has configured, including `AuthorizationOptions.FallbackPolicy`. So if your app does this:

```csharp
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
```

…the upload API requires an authenticated user too, with nothing extra to wire up.

If your app configures no authentication, or no fallback policy, the endpoints stay open exactly as they are today — nothing to change.

If you *want* anonymous uploads in an app that otherwise requires authentication, say so explicitly:

```csharp
builder.Services.AddKillaCoreFileUploadServer(builder.Configuration, allowAnonymousUploads: true);
```

> **⚠️ Upgrading from an earlier version:** the upload action used to carry `[AllowAnonymous]`. Because that attribute is explicit authorization metadata, it *overrode* a fallback policy — an application that required an authenticated user everywhere still had its file-receiving endpoint reachable anonymously, and could not close it without replacing the controller. If your uploads were anonymous by design **and** your app has a fallback policy, set `allowAnonymousUploads: true` when you upgrade.

### Serving uploaded content

Validating an upload establishes what a file *is*. It says nothing about what happens when you serve it back. A file served from your application's own origin runs in that origin's security context, so an uploaded `.html` — or anything a browser will sniff as markup — becomes stored XSS against your own users.

That is why `text/html` is **not** in the default `AllowedMimeTypes`. HTML carries no magic number, so content inspection can only confirm that the bytes are text; the server cannot tell a document from an attack.

If you serve uploaded files back to users, do at least one of the following, whichever types you allow:

* serve them from a **separate origin** — a different domain, not merely a different path
* send `Content-Disposition: attachment` so the browser downloads instead of rendering
* send `X-Content-Type-Options: nosniff` together with a `Content-Type` **you** decide, never the one the client supplied

This is the consuming application's responsibility: the package never serves files, so it cannot do it for you.

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