# KillaCore.Blazor.FileUpload

A high-performance, secure, and modern file upload pipeline designed for Blazor Server, Blazor WebAssembly, and Blazor WebApp (Auto) applications. 

This library solves the fundamental issue of Blazor SignalR message size limits by offloading the actual data transfer to a dedicated Javascript worker (`XMLHttpRequest`) and Web API pipeline. It strictly separates the UI state machine from server-side CPU-bound operations (hashing, duplicate verification, and disk saving), ensuring your Blazor UI remains highly responsive regardless of file size.

## 🚀 Key Features

* **Dual-Package Architecture:** Seamlessly supports .NET 10 Blazor WebApp Auto rendering modes by strictly separating Client UI logic from Server API logic.
* **SignalR Bypass:** Transfers files directly to an API controller, completely preventing Blazor Server circuit disconnects and memory spikes.
* **Multi-Tier Duplicate Detection:** Saves bandwidth and server resources by intelligently skipping identical files within the same batch using instant client-side heuristics and server-side SHA-256 cryptographic hashing.
* **End-to-End Security:**
  * **Anti-Replay Tokens:** Uses short-lived, one-time-use secure tokens to prevent unauthorized API access.
  * **Server-Authoritative Limits:** File size and allowed MIME types live in server configuration and are enforced there, with optional per-context profiles. The client holds matching values only to fail fast; it cannot widen them.
  * **Magic Number Inspection:** Validates actual file content headers on the server, ensuring users cannot bypass security by renaming file extensions.
* **Extensible Server Hooks:** A clean `IFileUploadServerHooks` interface allows you to execute custom logic (saving to DB, AWS S3, Azure Blob, or local disk) without altering the core pipeline.
* **Resiliency & Cancellation:** Full support for `CancellationToken` propagation. Cancelling a batch in the UI immediately halts network requests and server-side saving operations.
* **Orphan Cleanup Janitor:** Includes a lightweight, zero-impact BackgroundService that automatically sweeps the temporary storage folder to prevent disk exhaustion in the event of hard server crashes or IIS App Pool recycles.

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
    builder.Services.AddKillaCoreFileUploadServer(builder.Configuration);
    builder.Services.AddKillaCoreFileUploadClient(); // If prerendering Client components
    
    // 3. Register your custom server hooks (See Step 4)
    builder.Services.AddScoped<IFileUploadServerHooks, ApplicationUploadHooks>();
    ```

#### Option B: Blazor Server (Interactive Server Only)
Since everything runs on one machine, register everything in your single `Program.cs`:
```csharp
builder.Services.AddControllers();

builder.Services.AddKillaCoreFileUploadServer(builder.Configuration);
builder.Services.AddKillaCoreFileUploadClient();

builder.Services.AddScoped<IFileUploadServerHooks, ApplicationUploadHooks>();
```

### 3. Map the Controllers (Server Project)
Ensure your backend is set up to route requests to the package's internal `UploadsController`.
```csharp
app.MapControllers(); // Ensure this is before app.Run()
```

### 4. Configure `appsettings.json` (Server Project)
Define the signing key and the upload limits. **The limits are enforced here, not in the browser** — the component's matching options only decide how quickly a user is told.

```json
{
  "KillaCoreFileUpload": {
    "SecretKey": "Your-Super-Secure-Secret-Key-Min-16-Chars",
    "TempFolder": "KillaCoreUploads",

    "MaxFileBytes": 52428800,
    "AllowedMimeTypes": [ "image/jpeg", "image/png", "application/pdf" ],
    "MaxRequestBytes": 524288000,

    "Profiles": {
      "Photos": { "MaxFileBytes": 10485760, "AllowedMimeTypes": [ "image/jpeg", "image/png" ] }
    }
  }
}
```

| Key | Default | Meaning |
| :--- | :--- | :--- |
| `SecretKey` | *required* | Signs the one-time upload tokens. Minimum 16 characters. |
| `TempFolder` | `KillaCoreUploads` | Where the **default, local-disk** quarantine holds files while they are checked. A bare name lands in the OS temp directory; an absolute path such as `D:\TempUploads` is used as given. Ignored if you register your own store. |
| `MaxFileBytes` | 50 MB | Largest accepted file. **This is the authoritative limit.** |
| `AllowedMimeTypes` | images, PDF, Office | Accepted types, checked against the file's actual content. Omit the key for the built-in list; an empty array means any type. |
| `MaxRequestBytes` | 500 MB | Transport ceiling for Kestrel and multipart parsing — what aborts an over-sized request mid-stream. Keep at or above the largest per-file limit. |
| `StreamDirectToStorage` | `false` | Hand `SaveFileAsync` the request body itself instead of a completed temp file. See *Buffered or direct* below. |
| `Profiles` | none | Per-context overrides. |

**Profiles** are keyed by the component's `UploadContext`, the same key that selects your `IFileUploadServerHooks`. A context with no profile uses the root values, and a profile may override the size, the types, the streaming mode, or any combination. The client chooses among the profiles *you* defined; it cannot describe one.

#### Buffered or direct

The request body is read as a stream either way — nothing is buffered to disk before validation, and a file whose signature is wrong is rejected after 8 KB rather than after its full length. What differs is where the bytes go.

**Buffered (default).** Body → `TempFolder` → hook. The SHA-256 is computed on the way past, so there is no second pass. Costs one extra write and read, and buys three things:

* a seekable stream with a known `Length` for `SaveFileAsync` — which S3 and Azure SDKs use to choose single-part vs multipart
* the hash and file size **before** saving, so `CheckRemoteDuplicateAsync` can run
* the ability to decline a file without having written it anywhere

**Direct (`StreamDirectToStorage: true`).** Body → hook. One write. The hash and size are by-products of the hook's own copy, so they exist only *after* it has drained the stream — and with nothing held anywhere, **both duplicate checks are skipped for that context**. Identical content uploaded twice will be stored twice. The stream is forward-only and `Length` throws.

Use direct when storage is remote and your disk is small, and duplicate detection is not something you rely on.

#### Where buffered uploads wait

The quarantine defaults to local disk under `TempFolder` — fast, and right for most applications. It does not scale, though: at peak a server needs *concurrent users x `MaxConcurrentUploads` x file size* of scratch space. Twenty people uploading five 200 MB files at once is 20 GB, on a container that may have two.

Register your own store to move that pressure off the machine:

```csharp
builder.Services.AddSingleton<IUploadQuarantineStore, S3QuarantineStore>();
```

Either order works: the built-in is registered with `TryAdd`, and a later registration wins anyway.

```csharp
public interface IUploadQuarantineStore
{
    Task<string> WriteAsync(FileTransferData data, Stream content, CancellationToken ct); // -> handle
    Task<Stream> OpenReadAsync(string handle, CancellationToken ct);
    Task DeleteAsync(string handle, CancellationToken ct);

    // Optional: the background janitor calls this. Default does nothing, which is correct for a
    // store whose backing service expires objects on its own.
    Task SweepOrphansAsync(TimeSpan maxAge, CancellationToken ct) => Task.CompletedTask;
}
```

**Avoid the round trip when both ends are the same service.** A remote quarantine otherwise costs three transfers: into quarantine, back out, and on to permanent storage. The handle is on the model, so a hook can promote the file with a server-side copy and never read the stream at all:

```csharp
public async Task SaveFileAsync(FileTransferData data, Stream fileStream, CancellationToken ct)
{
    if (data.QuarantineHandle is { } handle && handle.StartsWith("s3://"))
    {
        await _s3.CopyObjectAsync(/* quarantine -> permanent */);   // metadata only, no bytes move
        return;
    }

    await using var fs = File.Create(destination);
    await fileStream.CopyToAsync(fs, ct);
}
```

`QuarantineHandle` is valid only for the duration of `SaveFileAsync`, is never serialised to the browser, and is null for a context that streams directly to storage.

**The client-side limits still exist** on `FileProcessingOptions`, as a fallback for the moment before the server's limits are fetched. Stricter than the server is fine — the user is simply told sooner. Looser only wastes bandwidth: the file transfers in full and is then rejected.

Omit the limit keys entirely and the built-in defaults apply, with a warning logged at startup so the fallback is never silent.

---

### 5. Implement Server Hooks (Your Custom Logic)

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
    //
    //    For files that reached the server these are the SERVER's records, so DetectedHash and
    //    FileSize are the values it computed, not whatever the browser sent back. Files the
    //    client skipped locally never reached the server, so their DetectedHash is null.
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
        AllowedMimeTypes = ["image/jpeg", "image/png", "application/pdf"]
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
| `MaxFiles` | `10` | Maximum number of files **accepted** per batch. Extras are marked `Skipped` with a `FileSkipped` event rather than rejected outright, so pass `int.MaxValue` to `GetMultipleFiles` and let the component enforce this limit. |
| `MaxSizeFileBytes` | `50 MB` | Fallback for fast UI feedback, used until the server's limit is fetched. The server's `MaxFileBytes` is the real limit. |
| `AllowedMimeTypes` | images, PDF, Office formats | Fallback for fast UI feedback, used until the server's list is fetched. The server's `AllowedMimeTypes` is the real rule. `text/html` is **not** in either default — see *Serving uploaded content* below. |
| `MaxConcurrentUploads` | `5` | Throttle limit for how many parallel XHR network requests are executed. |
| `VerifyLocalDuplicates` | `true` | Skip a file when an earlier file in the same selection has the same name and size. A client-side convenience only — the server rejects duplicate **content** by SHA-256 either way. |
| `UploadContext` | `"Default"` | Selects which keyed `IFileUploadServerHooks` handles the batch. Must match the key used in `AddKeyedScoped`. |
| `ServerBaseUrl` | `null` | Set only when the API is on a different origin — see *Cross-Origin Deployments*. Leave null for a same-project API. |

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

### Authenticating uploads

The pipeline makes two kinds of request, and they authenticate differently.

**The JSON calls** — policy, token, batch completion — go through a typed `HttpClient`, so attach your application's usual handler:

```csharp
builder.Services.AddKillaCoreFileUploadClient()
                .AddHttpMessageHandler<AuthorizationMessageHandler>();
```

**The file transfer** is an `XMLHttpRequest` driven by the component, so no message handler touches it. Its `X-Upload-Token` proves the request came through this pipeline; it does **not** establish who the user is. Supply that explicitly:

```razor
<FileUploadProcessor @ref="_processor"
                     InputSelector="#myFileInput"
                     Options="_options"
                     GetUploadHeadersAsync="GetAuthHeadersAsync" />

@code {
    private async Task<IReadOnlyDictionary<string, string>> GetAuthHeadersAsync()
    {
        var token = await TokenProvider.GetAccessTokenAsync();
        return new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
    }
}
```

`GetUploadHeadersAsync` is called **once per batch**, before the first file starts. A batch of large files can run for minutes, so a short-lived token may expire before the last file is sent — lower `MaxFiles` if that is a risk in your deployment. If it throws, the batch fails before anything is sent rather than producing one 401 per file. The package's own header names are reserved and ignored if returned.

For **cookie** authentication set `SendCredentials="true"` instead. It is only needed cross-origin — a same-origin upload always carries cookies — and the API's CORS policy must then add `.AllowCredentials()`.

| | Same-origin | Cross-origin |
| :--- | :--- | :--- |
| Cookies | automatic | `SendCredentials` + `AllowCredentials()` |
| Bearer token | `GetUploadHeadersAsync` | `GetUploadHeadersAsync` |

### Serving uploaded content

Validating an upload establishes what a file *is*. It says nothing about what happens when you serve it back. A file served from your application's own origin runs in that origin's security context, so an uploaded `.html` — or anything a browser will sniff as markup — becomes stored XSS against your own users.

That is why `text/html` is **not** in the default `AllowedMimeTypes`. HTML carries no magic number, so content inspection can only confirm that the bytes are text; the server cannot tell a document from an attack.

If you serve uploaded files back to users, do at least one of the following, whichever types you allow:

* serve them from a **separate origin** — a different domain, not merely a different path
* send `Content-Disposition: attachment` so the browser downloads instead of rendering
* send `X-Content-Type-Options: nosniff` together with a `Content-Type` **you** decide, never the one the client supplied

This is the consuming application's responsibility: the package never serves files, so it cannot do it for you.

### IIS & Web Server Request Limits
By default, ASP.NET Kestrel and IIS block HTTP POST requests larger than **30MB**, and will reject the request with `413 Payload Too Large` before it ever reaches the `UploadsController`.

**Kestrel and multipart parsing are handled for you** — `AddKillaCoreFileUploadServer` applies `MaxRequestBytes` from your configuration to both, so raising that one key is enough. (It replaces a hardcoded 500MB ceiling that could not be changed at all.)

**IIS is not**, because its request filter runs in native code before ASP.NET sees the request. Raise `maxAllowedContentLength` to at least your `MaxRequestBytes`:

```xml
<security>
  <requestFiltering>
    <requestLimits maxAllowedContentLength="524288000" />
  </requestFiltering>
</security>
```

A `413` carrying a message names the limit that rejected it; a `413` with an empty body came from IIS or Kestrel before the application was reached.

### Cross-Origin Deployments (standalone WASM + separate API)

When the Blazor app and the upload API live on different origins, set `ServerBaseUrl` and nothing else. Every call in the pipeline — policy, token, upload, batch completion — is then addressed to that host:

```csharp
private readonly FileProcessingOptions _options = new()
{
    ServerBaseUrl = "https://api.example.com"
};
```

Sending them all to one host is not a convenience, it is a requirement: the upload token is signed by one server and verified by the same one, and the limits must come from the server that enforces them. Splitting them across two hosts fails on the token signature.

You must also allow the package's headers in the API's CORS policy, or the browser's preflight fails before anything is uploaded. The list is exposed so you don't have to keep it in sync by hand:

```csharp
builder.Services.AddCors(options => options.AddPolicy("KillaCoreUploads", policy => policy
    .WithOrigins("https://app.example.com")
    .WithHeaders(FileUploadConstants.CORS_REQUEST_HEADERS)
    .WithMethods("GET", "POST")));

// ...before app.MapControllers()
app.UseCors("KillaCoreUploads");
```

Add `.AllowCredentials()` **only** if you also set `SendCredentials` on the component — by default nothing in the pipeline sends cookies, so it would grant a permission that is never used. The explicit `WithOrigins(...)` is required either way, because browsers reject credentialed requests against `AllowAnyOrigin()`. See *Authenticating uploads* above.

> Leave `ServerBaseUrl` null when the app and the API are the same project. Routes then stay relative, which also keeps apps hosted under a sub-path such as `https://host/apps/myapp/` working.

### Web Farms & Docker Swarm
Upload tokens are signed with your shared `SecretKey`, so any server can verify a token another one issued. There is no Data Protection key ring to synchronise.

What *is* per-process is the anti-replay and in-batch duplicate state, held in `IMemoryCache`. Behind a load balancer each server keeps its own, so a one-time token could be replayed once per server, and in-batch duplicate detection only sees the files that happened to land on the same node. Either enable sticky sessions, or register your own `IBatchDuplicateTracker` backed by a distributed store with an atomic insert-if-absent such as Redis `SET NX` — the interface is public for exactly that reason.

---
## 📄 License
MIT License.