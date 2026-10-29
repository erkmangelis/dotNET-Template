using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Features.Auth;

/// <summary>
/// Copies the roles from where the provider keeps them (<see cref="OidcOptions.RoleClaim"/>) into
/// standard role claims, so <c>RequireRole</c> and the permission checks work the same for every provider.
/// </summary>
internal sealed class OidcRoleClaims(IOptions<OidcOptions> options) : IClaimsTransformation
{
    private const string TransformedType = "oidc_roles_mapped";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(claim => claim.Type == TransformedType))
        {
            return Task.FromResult(principal);
        }

        foreach (var role in ReadRoles(identity, options.Value.RoleClaim))
        {
            if (!identity.HasClaim(identity.RoleClaimType, role))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
            }
        }

        identity.AddClaim(new Claim(TransformedType, "true"));
        return Task.FromResult(principal);
    }

    internal static IEnumerable<string> ReadRoles(ClaimsIdentity identity, string roleClaim)
    {
        var path = roleClaim.Split('.');
        if (path.Length == 1)
        {
            return [.. identity.FindAll(roleClaim).Select(claim => claim.Value)];
        }

        var roles = new List<string>();
        foreach (var claim in identity.FindAll(path[0]))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                var element = document.RootElement;
                foreach (var segment in path[1..])
                {
                    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                    {
                        element = default;
                        break;
                    }
                }

                if (element.ValueKind == JsonValueKind.Array)
                {
                    roles.AddRange(element.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
                }
            }
            catch (JsonException)
            {
                // Not JSON, so it holds no nested roles.
            }
        }

        return roles;
    }
}
