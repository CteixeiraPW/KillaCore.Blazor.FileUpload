namespace KillaCore.Blazor.FileUpload.Services;

public interface IFileUploadBridgeService
{
    /// <summary>
    /// Registers a file that was just uploaded by the Controller.
    /// </summary>
    void RegisterFile(string token, string tempFilePath);

    /// <summary>
    /// Blazor calls this to claim the file. Returns true if found and not expired.
    /// IMPORTANT: This removes the file from the bridge (One-Time Access).
    /// </summary>
    bool TryClaimFile(string token, out string? tempFilePath);

    /// <summary>
    /// Registers a "Nonce" (Unique ID) from a security token to prevent Replay Attacks.
    /// Returns FALSE if the ID was already used.
    /// </summary>
    bool RegisterUsedKey(string uniqueId);
}
