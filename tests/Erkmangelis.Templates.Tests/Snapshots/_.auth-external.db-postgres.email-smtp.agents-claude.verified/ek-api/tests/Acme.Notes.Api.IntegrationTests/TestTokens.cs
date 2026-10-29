using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>Signs tokens the way a real provider would, with a key the test API trusts.</summary>
internal static class TestTokens
{
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string GoogleClientId = "test-client.apps.googleusercontent.com";

    public static RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = "trusted" };

    public static RsaSecurityKey UntrustedKey { get; } = new(RSA.Create(2048)) { KeyId = "trusted" };

    public static string Create(
        string issuer,
        string audience,
        Dictionary<string, object> claims,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = expiresAt.AddMinutes(-15),
            IssuedAt = expiresAt.AddMinutes(-15),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <summary>Stands in for every provider's discovery document: same issuer as Google, the test signing key.</summary>
internal sealed class TestProviderMetadata : IExternalProviderMetadata
{
    public Task<OpenIdConnectConfiguration> GetAsync(string authority, CancellationToken cancellationToken)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = authority };
        configuration.SigningKeys.Add(TestTokens.SigningKey);
        return Task.FromResult(configuration);
    }
}
