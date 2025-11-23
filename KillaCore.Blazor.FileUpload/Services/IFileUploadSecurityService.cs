namespace KillaCore.Blazor.FileUpload.Services;

public interface IFileUploadSecurityService
{
    /// <summary>
    /// Generates a signed, time-limited token for a specific file upload.
    /// </summary>
    /// <param name="fileId">The unique ID of the file or transfer session.</param>
    /// <param name="userId">The ID of the user performing the upload.</param>
    /// <returns>A Base64 encoded token string.</returns>
    string GenerateToken(string fileId, string userId);

    /// <summary>
    /// Validates a token's integrity, expiration, and ownership.
    /// </summary>
    /// <param name="token">The token string received from the client.</param>
    /// <param name="expectedUserId">The ID of the current authenticated user (to prevent token theft).</param>
    /// <param name="fileId">Outputs the File ID contained in the token if valid.</param>
    /// <returns>True if valid; otherwise false.</returns>
    bool ValidateToken(string token, string expectedUserId, out string? fileId);
}