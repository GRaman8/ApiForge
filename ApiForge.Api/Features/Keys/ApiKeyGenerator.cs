using System.Security.Cryptography;
using System.Text;

namespace ApiForge.Api.Features.Keys;

public static class ApiKeyGenerator
{
    public const string Prefix = "apf_live_";

    // Generates a cryptographically random API key. Returns the plaintext (shown to the user
    // exactly once), its SHA-256 hash (stored), and the human-visible prefix.
    public static (string plaintext, string hash, string prefix) Generate()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var rawKey = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        rawKey = rawKey[..Math.Min(40, rawKey.Length)];

        var plaintext = $"{Prefix}{rawKey}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

        return (plaintext, hash, Prefix);
    }
}
