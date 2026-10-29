using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.ApiKeys;

/// <summary>
/// The <c>ApiKey</c> authentication scheme, reading the <c>X-Api-Key</c> header. It is not the default
/// scheme: only endpoints whose policy names it accept keys, so a key can never manage keys, change the
/// account or reach admin endpoints.
/// </summary>
internal sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggers,
    UrlEncoder encoder,
    AppDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggers, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string KeyIdClaimType = "api_key_id";

    private static readonly TimeSpan _lastUsedResolution = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header))
        {
            return AuthenticateResult.NoResult();
        }

        if (header.Count != 1 || !ApiKeyFormat.TryParse(header[0]!, out var lookupId, out var secret))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var now = time.GetUtcNow();
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.LookupId == lookupId, Context.RequestAborted);
        if (key is null || !ApiKeyFormat.Matches(secret, key.SecretHash) || !key.IsActive(now))
        {
            LogRejected(Logger, lookupId);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > _lastUsedResolution)
        {
            await db.ApiKeys.Where(candidate => candidate.Id == key.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LastUsedAt, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, key.OwnerId),
                new Claim(KeyIdClaimType, key.Id.ToString()),
                .. key.Scopes.Select(scope => new Claim(ApiKeyScopes.ClaimType, scope)),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "API key {LookupId} was refused")]
    private static partial void LogRejected(ILogger logger, string lookupId);
}

internal static class ApiKeyPolicies
{
    /// <summary>
    /// Adds a policy for endpoints that accept both signed-in users and API keys. Users pass as they are;
    /// keys pass only with <paramref name="scope"/>.
    /// </summary>
    public static IServiceCollection AddUserOrKeyPolicy(this IServiceCollection services, string policyName, string scope)
    {
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<AuthenticationOptions>>((authorization, authentication) =>
                authorization.AddPolicy(policyName, policy => policy
                    .AddAuthenticationSchemes(authentication.Value.DefaultScheme!, ApiKeyAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => !IsApiKey(context.User) || context.User.HasClaim(ApiKeyScopes.ClaimType, scope))));

        return services;
    }

    public static bool IsApiKey(ClaimsPrincipal user) => user.HasClaim(claim => claim.Type == ApiKeyAuthenticationHandler.KeyIdClaimType);
}
