namespace KillaCore.Blazor.FileUpload.Models;

public class FileUploadServerOptions
{
    public const string DefaultConfigSectionName = "KillaCoreFileUpload";

    /// <summary>
    /// The secret key used to sign HMAC Anti-Replay tokens. Must be at least 16 characters.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The folder to store temporary files during the upload process. 
    /// If an absolute path is provided (e.g., "D:\TempUploads"), it will be used directly.
    /// If a relative name is provided, it will be placed inside the OS's default Temp directory.
    /// </summary>
    public string TempFolder { get; set; } = "KillaCoreUploads";
}