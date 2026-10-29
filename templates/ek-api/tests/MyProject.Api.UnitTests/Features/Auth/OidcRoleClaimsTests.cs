using System.Security.Claims;

namespace MyProject.Api.UnitTests.Features.Auth;

public sealed class OidcRoleClaimsTests
{
    [Fact]
    public async Task Keycloak_realm_roles_become_role_claims()
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", "alice"), new Claim("realm_access", """{"roles":["admin","offline_access"]}""")],
            authenticationType: "Bearer");

        var principal = await Transform(identity, "realm_access.roles");

        Assert.True(principal.IsInRole("admin"));
        Assert.True(principal.IsInRole("offline_access"));
    }

    [Fact]
    public async Task A_flat_claim_is_read_as_is()
    {
        var identity = new ClaimsIdentity([new Claim("groups", "editors")], authenticationType: "Bearer");

        var principal = await Transform(identity, "groups");

        Assert.True(principal.IsInRole("editors"));
    }

    [Fact]
    public async Task A_claim_that_is_not_json_adds_no_roles()
    {
        var identity = new ClaimsIdentity([new Claim("realm_access", "not json")], authenticationType: "Bearer");

        var principal = await Transform(identity, "realm_access.roles");

        Assert.Empty(principal.FindAll(ClaimTypes.Role));
    }

    private static Task<ClaimsPrincipal> Transform(ClaimsIdentity identity, string roleClaim)
    {
        OidcRoleClaims.AddRoles(identity, roleClaim);
        return Task.FromResult(new ClaimsPrincipal(identity));
    }
}
