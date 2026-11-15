using System.Security.Claims;

namespace Acme.Notes.Api.Features.Auth;

internal static class UserClaims
{
    /// <summary>The signed-in user's id: the Identity user id.</summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The endpoint must require an authenticated user.");
}
