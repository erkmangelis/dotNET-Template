using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Acme.Notes.Api.Features.Auth;

/// <summary>Roles a user can hold. Identity seeds them in the database; an OpenID Connect provider puts them in tokens.</summary>
internal static class Roles
{
    public const string Admin = "admin";

    /// <summary>Read-only access to everyone's data, for support and audits.</summary>
    public const string Auditor = "auditor";

    public static readonly string[] All = [Admin, Auditor];
}

/// <summary>
/// What a role may do. Endpoints check permissions, never role names, so changing who can do what is
/// an edit to <see cref="ByRole"/> alone. Add a permission here when you add an endpoint that needs one.
/// </summary>
internal static class Permissions
{
    public const string ClaimType = "permission";

    public const string NotesReadAll = "notes.read.all";
    public const string AuditRead = "audit.read";
    public const string UsersRead = "users.read";
    public const string UsersManageRoles = "users.roles.manage";
    public const string JobsManage = "jobs.manage";

    public static readonly IReadOnlyDictionary<string, string[]> ByRole = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Roles.Admin] =
        [
            NotesReadAll,
            AuditRead,
            UsersRead,
            UsersManageRoles,
            JobsManage,
        ],
        [Roles.Auditor] = [NotesReadAll, AuditRead, UsersRead],
    };

    /// <summary>Lets only callers with <paramref name="permission"/> through; others get 403, or 401 when signed out.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireClaim(ClaimType, permission));
}

/// <summary>Turns each role claim into its permission claims, once per request.</summary>
internal sealed class PermissionClaims : IClaimsTransformation
{
    // Marks a principal as done, so one with no permissions is not transformed again.
    private const string MappedClaimType = "permissions_mapped";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(claim => claim.Type == MappedClaimType))
        {
            return Task.FromResult(principal);
        }

        var permissions = identity.FindAll(identity.RoleClaimType)
            .SelectMany(role => Permissions.ByRole.TryGetValue(role.Value, out var granted) ? granted : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        identity.AddClaim(new Claim(MappedClaimType, "true"));
        foreach (var permission in permissions)
        {
            identity.AddClaim(new Claim(Permissions.ClaimType, permission));
        }

        return Task.FromResult(principal);
    }
}
