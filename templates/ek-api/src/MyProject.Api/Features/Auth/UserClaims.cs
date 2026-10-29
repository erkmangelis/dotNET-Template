using System.Security.Claims;

namespace MyProject.Api.Features.Auth;

internal static class UserClaims
{
#if (UseOidc)
    /// <summary>The signed-in user's id: the <c>sub</c> claim of the provider's access token.</summary>
#else
    /// <summary>The signed-in user's id: the Identity user id.</summary>
#endif
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The endpoint must require an authenticated user.");
}
