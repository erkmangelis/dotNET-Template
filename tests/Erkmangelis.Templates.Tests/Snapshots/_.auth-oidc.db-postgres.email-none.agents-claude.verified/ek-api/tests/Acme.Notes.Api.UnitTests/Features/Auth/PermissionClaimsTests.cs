using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.UnitTests.Features.Auth;

public sealed class PermissionClaimsTests
{
    [Fact]
    public async Task Each_role_adds_its_permissions()
    {
        var principal = await TransformAsync(Roles.Auditor);

        Assert.True(principal.HasClaim(Permissions.ClaimType, Permissions.NotesReadAll));
    }

    [Fact]
    public async Task No_role_means_no_permissions()
    {
        var principal = await TransformAsync();

        Assert.DoesNotContain(principal.FindAll(Permissions.ClaimType), claim => claim.Value.Length > 0);
    }

    [Fact]
    public async Task A_role_unknown_to_the_app_grants_nothing()
    {
        var principal = await TransformAsync("offline_access");

        Assert.DoesNotContain(principal.FindAll(Permissions.ClaimType), claim => claim.Value.Length > 0);
    }

    [Fact]
    public void Every_role_has_its_permissions_listed()
    {
        Assert.All(Roles.All, role => Assert.True(Permissions.ByRole.ContainsKey(role), $"{role} has no entry in Permissions.ByRole."));
    }

    [Fact]
    public async Task Signed_out_principals_are_left_alone()
    {
        var principal = await NewTransformation().TransformAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Empty(principal.Claims);
    }

    private static Task<ClaimsPrincipal> TransformAsync(params string[] roles)
    {
        var identity = new ClaimsIdentity([.. roles.Select(role => new Claim(ClaimTypes.Role, role))], authenticationType: "Test");
        return NewTransformation().TransformAsync(new ClaimsPrincipal(identity));
    }

    private static PermissionClaims NewTransformation() => new(Options.Create(new OidcOptions()));
}
