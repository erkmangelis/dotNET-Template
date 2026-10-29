using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Features.Auth;

internal static class OidcSetup
{
    /// <summary>
    /// Accounts live in a hosted OpenID Connect provider. The API is a resource server: it validates
    /// the provider's access tokens (issuer, audience, lifetime and signature) and never sees passwords.
    /// </summary>
    public static WebApplicationBuilder AddOidcAuth(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<OidcOptions>()
            .BindConfiguration(OidcOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(OidcOptions.AreValid, "Oidc:Authority must be an absolute https URL (http only with Oidc:RequireHttpsMetadata=false).")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<OidcOptions>>((jwt, oidc) =>
            {
                jwt.Authority = oidc.Value.Authority;
                jwt.Audience = oidc.Value.Audience;
                jwt.RequireHttpsMetadata = oidc.Value.RequireHttpsMetadata;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
            });

        services.AddAuthorization();
        services.AddTransient<IClaimsTransformation, OidcRoleClaims>();

        return builder;
    }
}
