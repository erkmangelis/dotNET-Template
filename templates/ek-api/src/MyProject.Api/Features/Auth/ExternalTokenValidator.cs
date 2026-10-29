using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MyProject.Api.Features.Auth;

/// <summary>Who the provider says signed in. <see cref="EmailVerified"/> is true only when the provider vouches for the email.</summary>
internal sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified);

/// <summary>The provider's issuer and signing keys. Swapped for fixed keys in tests.</summary>
internal interface IExternalProviderMetadata
{
    Task<OpenIdConnectConfiguration> GetAsync(string authority, CancellationToken cancellationToken);
}

/// <summary>Reads and caches each provider's discovery document and keys, refreshing them as the provider rotates keys.</summary>
internal sealed class ExternalProviderMetadata(IHttpClientFactory httpClients) : IExternalProviderMetadata
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);

    public Task<OpenIdConnectConfiguration> GetAsync(string authority, CancellationToken cancellationToken) =>
        _managers.GetOrAdd(authority, key => new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{key.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(httpClients.CreateClient(nameof(ExternalProviderMetadata))) { RequireHttps = true }))
            .GetConfigurationAsync(cancellationToken);
}

/// <summary>Validates an ID token from a configured provider: signature, issuer, audience, lifetime and nonce.</summary>
internal sealed partial class ExternalTokenValidator(
    IOptionsMonitor<ExternalAuthOptions> options,
    IExternalProviderMetadata metadata,
    ILogger<ExternalTokenValidator> logger)
{
    private static readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>The configured, enabled provider with this name, or null.</summary>
    public (string Name, ExternalProviderOptions Options)? FindProvider(string name) =>
        options.CurrentValue.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Key, name, StringComparison.OrdinalIgnoreCase) && provider.Value.IsEnabled) is { Key: not null } match
            ? (match.Key, match.Value)
            : null;

    public IEnumerable<string> EnabledProviders =>
        options.CurrentValue.Providers.Where(provider => provider.Value.IsEnabled).Select(provider => provider.Key);

    /// <summary>The identity in the token, or null when the token is not valid for this API.</summary>
    public async Task<ExternalIdentity?> ValidateAsync(string providerName, ExternalProviderOptions provider, string idToken, string? nonce, CancellationToken cancellationToken)
    {
        var configuration = await metadata.GetAsync(provider.Authority, cancellationToken);
        var result = await _handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidAudiences = provider.Audiences,
            IssuerSigningKeys = configuration.SigningKeys,
            IssuerValidator = (issuer, token, _) => ValidateIssuer(configuration.Issuer, issuer, token),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            LogRejected(logger, providerName, result.Exception?.GetType().Name ?? "invalid token");
            return null;
        }

        if ((provider.RequireNonce || nonce is not null) && !NonceMatches(token, nonce))
        {
            LogRejected(logger, providerName, "nonce mismatch");
            return null;
        }

        token.TryGetPayloadValue<string>("email", out var email);
        return new ExternalIdentity(providerName, token.Subject, string.IsNullOrWhiteSpace(email) ? null : email, IsTrue(token, provider.EmailVerifiedClaim));
    }

    // Microsoft's multi-tenant issuer is a template: https://login.microsoftonline.com/{tenantid}/v2.0.
    private static string ValidateIssuer(string expected, string issuer, SecurityToken token)
    {
        if (expected.Contains("{tenantid}", StringComparison.Ordinal)
            && token is JsonWebToken jwt
            && jwt.TryGetPayloadValue<string>("tid", out var tenantId))
        {
            expected = expected.Replace("{tenantid}", tenantId, StringComparison.Ordinal);
        }

        return string.Equals(expected, issuer, StringComparison.Ordinal)
            ? issuer
            : throw new SecurityTokenInvalidIssuerException("The token was not issued by the configured provider.");
    }

    private static bool NonceMatches(JsonWebToken token, string? nonce) =>
        nonce is not null
        && token.TryGetPayloadValue<string>("nonce", out var tokenNonce)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(nonce));

    // Providers send the flag as a JSON boolean (Google) or as the string "true" (Apple).
    private static bool IsTrue(JsonWebToken token, string claim) =>
        (token.TryGetPayloadValue<bool>(claim, out var flag) && flag)
        || (token.TryGetPayloadValue<string>(claim, out var text) && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Warning, Message = "External sign-in with {Provider} refused: {Reason}")]
    private static partial void LogRejected(ILogger logger, string provider, string reason);
}
