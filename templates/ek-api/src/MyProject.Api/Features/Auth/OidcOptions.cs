using System.ComponentModel.DataAnnotations;

namespace MyProject.Api.Features.Auth;

/// <summary>The hosted OpenID Connect provider that owns the accounts, bound from the <c>Oidc</c> section.</summary>
internal sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The issuer, for example <c>https://sso.example.com/realms/myproject</c> (Keycloak) or <c>https://login.microsoftonline.com/{tenant-id}/v2.0</c> (Entra ID).</summary>
    [Required]
    public string Authority { get; set; } = "";

    /// <summary>The <c>aud</c> value the provider puts in access tokens for this API.</summary>
    [Required]
    public string Audience { get; set; } = "";

    /// <summary>Fetch the provider's metadata over HTTPS only. Turn off only for a local provider in Development.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Where the token keeps the user's roles. A dotted path reads into a JSON claim, for example
    /// <c>realm_access.roles</c> for Keycloak realm roles. Entra ID app roles use <c>roles</c>.
    /// </summary>
    [Required]
    public string RoleClaim { get; set; } = "roles";

    internal static bool AreValid(OidcOptions options) =>
        Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
        && (authority.Scheme == Uri.UriSchemeHttps || (!options.RequireHttpsMetadata && authority.Scheme == Uri.UriSchemeHttp));
}
