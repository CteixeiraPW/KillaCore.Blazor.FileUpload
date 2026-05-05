namespace KillaCore.Blazor.FileUpload.Services;

public interface IFileUploadSecurityService
{
    /// <summary>
    /// Generates a signed, time-limited token for a specific file upload.
    /// </summary>
    string GenerateToken(string fileId, string userId);

    /// <summary>
    /// Validates a token's integrity and expiration.
    /// Extracts the embedded fileId and userId from the token payload.
    /// </summary>
    /// <param name="token">The token string received from the client.</param>
    /// <param name="fileId">The File ID embedded in the token.</param>
    /// <param name="userId">The User ID embedded in the token.</param>
    /// <returns>True if signature and expiration are valid.</returns>
    bool ValidateToken(string token, out string? fileId, out string? userId);

    /// <summary>
    /// Validates a token and additionally checks that the embedded userId matches an expected value.
    /// </summary>
    bool ValidateToken(string token, string expectedUserId, out string? fileId);
}