using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Features.Auth;

public sealed record AdminUserResponse(string Id, string? Email, bool EmailConfirmed, bool LockedOut, IReadOnlyList<string> Roles);

public sealed record SetRolesRequest
{
    [Required]
    [MaxLength(10)]
    public string[] Roles { get; init; } = [];
}

/// <summary>User administration under <c>/v1/admin/users</c>, guarded by permissions.</summary>
internal static class UserAdminEndpoints
{
    public static RouteGroupBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder admin)
    {
        var users = admin.MapGroup("/users");

        users.MapGet("/", ListAsync)
            .RequirePermission(Permissions.UsersRead)
            .WithSummary("List users with their roles");
        users.MapPut("/{id}/roles", SetRolesAsync)
            .RequirePermission(Permissions.UsersManageRoles)
            .WithSummary("Replace a user's roles");

        return users;
    }

    private static async Task<Ok<PagedResult<AdminUserResponse>>> ListAsync(
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        var total = await db.Users.LongCountAsync(cancellationToken);
        var users = await db.Users.AsNoTracking()
            .OrderBy(user => user.Email)
            .ThenBy(user => user.Id)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .Select(user => new { user.Id, user.Email, user.EmailConfirmed, user.LockoutEnd })
            .ToListAsync(cancellationToken);

        var ids = users.Select(user => user.Id).ToList();
        var roles = await db.UserRoles
            .Where(userRole => ids.Contains(userRole.UserId))
            .Join(db.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        var items = users.Select(user => new AdminUserResponse(
                user.Id,
                user.Email,
                user.EmailConfirmed,
                user.LockoutEnd > now,
                [.. roles.Where(role => role.UserId == user.Id).Select(role => role.Name!).Order(StringComparer.Ordinal)]))
            .ToList();

        return TypedResults.Ok(new PagedResult<AdminUserResponse>(items, page, pageSize, total));
    }

    private static async Task<Results<Ok<AdminUserResponse>, NotFound, ValidationProblem, ProblemHttpResult>> SetRolesAsync(
        string id,
        SetRolesRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        TimeProvider time)
    {
        var unknown = request.Roles.Except(Roles.All, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SetRolesRequest.Roles)] = [$"Unknown roles: {string.Join(", ", unknown)}. Known roles: {string.Join(", ", Roles.All)}."],
            });
        }

        if (await users.FindByIdAsync(id) is not { } user)
        {
            return TypedResults.NotFound();
        }

        var current = await users.GetRolesAsync(user);
        if (user.Id == principal.GetUserId() && current.Contains(Roles.Admin) && !request.Roles.Contains(Roles.Admin))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Cannot remove your own admin role",
                detail: "Ask another admin, so the app is never left without one.");
        }

        await users.RemoveFromRolesAsync(user, current.Except(request.Roles, StringComparer.Ordinal));
        await users.AddToRolesAsync(user, request.Roles.Distinct(StringComparer.Ordinal).Except(current, StringComparer.Ordinal));

        // New tokens carry the new roles; refresh tokens issued before this change stop working.
        await users.UpdateSecurityStampAsync(user);

        var roles = await users.GetRolesAsync(user);
        return TypedResults.Ok(new AdminUserResponse(
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.LockoutEnd > time.GetUtcNow(),
            [.. roles.Order(StringComparer.Ordinal)]));
    }
}
