using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acme.Notes.Api.Features.Webhooks;

internal enum WebhookVerification
{
    Valid,
    MissingHeaders,
    TimestampOutOfRange,
    BadSignature,
}

/// <summary>
/// Signatures as the Standard Webhooks specification defines them (standardwebhooks.com), for
/// what this app sends and what it receives. Three headers travel with the body:
/// <c>webhook-id</c> (unique per message, the same on every retry), <c>webhook-timestamp</c>
/// (Unix seconds) and <c>webhook-signature</c>: <c>v1,</c> and the Base64 HMAC-SHA256 of
/// <c>{id}.{timestamp}.{body}</c>, keyed with the secret's bytes. Several space-separated
/// signatures are allowed, so a secret can be rotated without dropping messages.
/// </summary>
internal static class StandardWebhooks
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    /// <summary>Secrets are <c>whsec_</c> and the Base64 of the key bytes.</summary>
    public const string SecretPrefix = "whsec_";

    private const string SignatureVersion = "v1";

    /// <summary>A new random secret with 32 bytes of key.</summary>
    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static bool IsValidSecret(string? secret) => secret is not null && TryKey(secret, out var key) && key.Length >= 24;

    /// <summary>The <c>webhook-signature</c> value for one message.</summary>
    public static string Sign(string secret, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        if (!TryKey(secret, out var key))
        {
            throw new ArgumentException("The secret is not whsec_ and Base64.", nameof(secret));
        }

        return $"{SignatureVersion},{Convert.ToBase64String(Hash(key, id, timestamp, body))}";
    }

    /// <summary>
    /// Checks a received message against any of <paramref name="secrets"/>. A timestamp further than
    /// <paramref name="tolerance"/> from <paramref name="now"/> fails, so a captured message cannot be replayed later.
    /// </summary>
    public static WebhookVerification Verify(
        IEnumerable<string> secrets,
        string? id,
        string? timestamp,
        string? signatures,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return WebhookVerification.MissingHeaders;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > tolerance.TotalSeconds)
        {
            return WebhookVerification.TimestampOutOfRange;
        }

        var received = signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(signature => signature.Split(',', 2))
            .Where(parts => parts.Length == 2 && parts[0] == SignatureVersion)
            .Select(parts => TryBase64(parts[1]))
            .OfType<byte[]>()
            .ToList();

        foreach (var secret in secrets)
        {
            if (!TryKey(secret, out var key))
            {
                continue;
            }

            var expected = Hash(key, id, seconds, body);
            if (received.Any(signature => CryptographicOperations.FixedTimeEquals(signature, expected)))
            {
                return WebhookVerification.Valid;
            }
        }

        return WebhookVerification.BadSignature;
    }

    private static byte[] Hash(byte[] key, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}."));
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        return HMACSHA256.HashData(key, content);
    }

    private static bool TryKey(string secret, out byte[] key)
    {
        var encoded = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        key = TryBase64(encoded) ?? [];
        return key.Length > 0;
    }

    private static byte[]? TryBase64(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) ? buffer[..written] : null;
    }
}
