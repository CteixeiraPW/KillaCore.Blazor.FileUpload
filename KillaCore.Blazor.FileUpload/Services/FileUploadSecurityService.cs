using KillaCore.Blazor.FileUpload.Models;
using Microsoft.Extensions.Options;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KillaCore.Blazor.FileUpload.Services;

internal class HmacFileUploadSecurityService : IFileUploadSecurityService
{
    private readonly byte[] _secretKeyBytes;
    private readonly TimeSpan _tokenLifespan = TimeSpan.FromMinutes(5);

    public HmacFileUploadSecurityService(IOptions<FileUploadServerOptions> options)
    {
        var secretKey = options.Value.SecretKey;

        if (string.IsNullOrWhiteSpace(secretKey) || secretKey.Length < 16)
        {
            throw new ArgumentException("KillaCoreFileUpload:SecretKey must be defined in appsettings.json and be at least 16 characters long.");
        }

        _secretKeyBytes = Encoding.UTF8.GetBytes(secretKey);
    }

    public string GenerateToken(string fileId, string userId)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_tokenLifespan).ToUnixTimeSeconds();

        var payload = new TokenPayload(fileId, userId, expiry);
        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));

        var signature = ComputeSignature(payloadBase64);

        // Final token: base64(json).signature (dot-separated, safe since Base64 and HMAC don't contain dots)
        var combined = $"{payloadBase64}.{signature}";
        return ToBase64Url(Encoding.UTF8.GetBytes(combined));
    }

    public bool ValidateToken(string token, out string? fileId, out string? userId)
    {
        fileId = null;
        userId = null;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var decodedString = Encoding.UTF8.GetString(FromBase64Url(token));

            // Split on '.' — exactly 2 parts: payload and signature
            var dotIndex = decodedString.LastIndexOf('.');
            if (dotIndex < 0)
                return false;

            var payloadBase64 = decodedString[..dotIndex];
            var providedSignature = decodedString[(dotIndex + 1)..];

            // 1. Verify Signature
            var computedSignature = ComputeSignature(payloadBase64);
            var computedBytes = Encoding.UTF8.GetBytes(computedSignature);
            var providedBytes = Encoding.UTF8.GetBytes(providedSignature);

            if (!CryptographicOperations.FixedTimeEquals(computedBytes, providedBytes))
                return false;

            // 2. Decode payload
            var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
            var payload = JsonSerializer.Deserialize<TokenPayload>(payloadJson);

            if (payload is null)
                return false;

            // 3. Check Expiration
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.Exp)
                return false;

            fileId = payload.FileId;
            userId = payload.UserId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ValidateToken(string token, string expectedUserId, out string? fileId)
    {
        if (!ValidateToken(token, out fileId, out string? userId))
            return false;

        if (!string.Equals(userId, expectedUserId, StringComparison.OrdinalIgnoreCase))
        {
            fileId = null;
            return false;
        }

        return true;
    }

    private string ComputeSignature(string data)
    {
        using var hmac = new HMACSHA256(_secretKeyBytes);
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hashBytes);
    }

    // --- Base64Url helpers (no padding, URL-safe) ---
    private static string ToBase64Url(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] FromBase64Url(string base64Url) => Base64Url.DecodeFromChars(base64Url);

    private sealed record TokenPayload(string FileId, string UserId, long Exp);
}