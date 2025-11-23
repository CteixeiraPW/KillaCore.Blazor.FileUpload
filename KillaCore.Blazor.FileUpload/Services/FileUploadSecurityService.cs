using System.Security.Cryptography;
using System.Text;

namespace KillaCore.Blazor.FileUpload.Services;

internal class HmacFileUploadSecurityService : IFileUploadSecurityService
{
    private readonly byte[] _secretKeyBytes;
    private readonly TimeSpan _tokenLifespan = TimeSpan.FromMinutes(5);

    // Constructor accepts the key as a string (e.g., from appsettings.json)
    public HmacFileUploadSecurityService(string secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Secret key cannot be empty", nameof(secretKey));

        // Ensure the key is at least somewhat secure length-wise
        if (secretKey.Length < 16)
            throw new ArgumentException("Secret key must be at least 16 characters long for security.");

        _secretKeyBytes = Encoding.UTF8.GetBytes(secretKey);
    }

    public string GenerateToken(string fileId, string userId)
    {
        // 1. Create the Payload
        // We include the FileId, UserId, and Expiration Timestamp
        var expiry = DateTimeOffset.UtcNow.Add(_tokenLifespan).ToUnixTimeSeconds();
        var payload = $"{fileId}:{userId}:{expiry}";

        // 2. Sign the Payload
        var signature = ComputeSignature(payload);

        // 3. Combine and Encode
        // Final Format: "fileId:userId:expiry:signature" (Base64 Encoded)
        var combined = $"{payload}:{signature}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(combined));
    }

    public bool ValidateToken(string token, string expectedUserId, out string? fileId)
    {
        fileId = null;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            // 1. Decode
            // Try/Catch handles invalid Base64 strings
            var decodedString = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var parts = decodedString.Split(':');

            // We expect 4 parts: [0]FileId, [1]UserId, [2]Expiry, [3]Signature
            if (parts.Length != 4)
                return false;

            var fId = parts[0];
            var uId = parts[1];
            var expString = parts[2];
            var providedSignature = parts[3];

            // 2. Check Ownership (Prevent User A from using User B's token)
            // Note: If expectedUserId is null (anonymous), skip this check or enforce it based on requirements
            if (!string.Equals(uId, expectedUserId, StringComparison.OrdinalIgnoreCase))
                return false;

            // 3. Check Expiration
            if (!long.TryParse(expString, out var exp))
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > exp)
                return false; // Expired

            // 4. Verify Signature (The most critical step)
            // Reconstruct the payload exactly as it was generated
            var payloadToVerify = $"{fId}:{uId}:{expString}";
            var computedSignature = ComputeSignature(payloadToVerify);

            // Secure string comparison to prevent timing attacks
            if (FixedTimeEquals(computedSignature, providedSignature))
            {
                fileId = fId;
                return true;
            }
        }
        catch
        {
            // Logging can be added here
            return false;
        }

        return false;
    }

    private string ComputeSignature(string payload)
    {
        using var hmac = new HMACSHA256(_secretKeyBytes);
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hashBytes);
    }

    // Prevents timing attacks by comparing all characters before returning
    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;

        int result = 0;
        for (int i = 0; i < left.Length; i++)
        {
            result |= left[i] ^ right[i];
        }
        return result == 0;
    }
}