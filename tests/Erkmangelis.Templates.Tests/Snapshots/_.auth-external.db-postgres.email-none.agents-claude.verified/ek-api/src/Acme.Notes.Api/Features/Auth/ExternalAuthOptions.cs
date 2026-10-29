namespace Acme.Notes.Api.Features.Auth;

/// <summary>Sign-in providers such as Google, Apple and Microsoft, bound from the <c>ExternalAuth</c> section.</summary>
internal sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    /// <summary>
    /// Providers by name. The name is the route segment (<c>/v1/auth/external/google</c>) and is stored with
    /// each linked login, so never rename one that is in use. A provider is on once it has a client id.
    /// </summary>
    public Dictionary<string, ExternalProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static bool AreValid(ExternalAuthOptions options) =>
        options.Providers.All(provider =>
            Uri.TryCreate(provider.Value.Authority, UriKind.Absolute, out var authority)
            && authority.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(provider.Value.EmailVerifiedClaim));
}

internal sealed class ExternalProviderOptions
{
    /// <summary>The provider's issuer; its keys are read from <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>
    /// The client ids your apps use with this provider (web, iOS, Android). An ID token is accepted only
    /// when its audience is one of them. Empty entries are ignored.
    /// </summary>
    public string[] ClientIds { get; set; } = [];

    /// <summary>
    /// The claim that says the provider checked the email address. Google and Apple use <c>email_verified</c>.
    /// For Microsoft, add the optional <c>xms_edov</c> claim to the app registration and name it here.
    /// </summary>
    public string EmailVerifiedClaim { get; set; } = "email_verified";

    /// <summary>Refuse ID tokens without a <c>nonce</c> that matches the one the client sends.</summary>
    public bool RequireNonce { get; set; }

    internal string[] Audiences => [.. ClientIds.Where(id => !string.IsNullOrWhiteSpace(id))];

    internal bool IsEnabled => Audiences.Length > 0;
}
