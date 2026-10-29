using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Acme.Notes.Api.Features.ApiKeys;

/// <summary>
/// Keys look like <c>ak_3f9c0e5a7b1d_&lt;43 characters&gt;</c>: a fixed prefix so secret scanners can spot
/// them, a public lookup id, and 256 random bits of secret.
/// </summary>
internal static class ApiKeyFormat
{
    public const string Prefix = "ak";
    private const int LookupIdLength = 12;

    public static (string Key, string LookupId, byte[] SecretHash) Generate()
    {
        var lookupId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(LookupIdLength / 2));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return ($"{Prefix}_{lookupId}_{secret}", lookupId, Hash(secret));
    }

    public static bool TryParse(string key, out string lookupId, out string secret)
    {
        lookupId = secret = "";
        var parts = key.Split('_', 3);
        if (parts.Length != 3 || parts[0] != Prefix || parts[1].Length != LookupIdLength || parts[2].Length < 32)
        {
            return false;
        }

        (lookupId, secret) = (parts[1], parts[2]);
        return true;
    }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public static bool Matches(string secret, byte[] secretHash) => CryptographicOperations.FixedTimeEquals(Hash(secret), secretHash);

    /// <summary>What a list shows instead of the key: enough to recognise it, nothing to use.</summary>
    public static string Hint(string lookupId) => $"{Prefix}_{lookupId}_…";
}
