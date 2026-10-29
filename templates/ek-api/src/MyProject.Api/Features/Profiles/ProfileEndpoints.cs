using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Features.Profiles;

public sealed partial record UpdateProfileRequest : IValidatableObject
{
    [StringLength(UserProfile.DisplayNameMaxLength)]
    public string? DisplayName { get; init; }

    [Required]
    [StringLength(64)]
    public string TimeZone { get; init; } = UserProfile.Defaults.TimeZone;

    [Required]
    [StringLength(35)]
    public string Locale { get; init; } = UserProfile.Defaults.Locale;

    [Required]
    public string Theme { get; init; } = Themes.System;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            yield return new ValidationResult("TimeZone must be an IANA time zone, for example Europe/Istanbul.", [nameof(TimeZone)]);
        }

        if (!LanguageTag().IsMatch(Locale))
        {
            yield return new ValidationResult("Locale must be a language tag, for example en or tr-TR.", [nameof(Locale)]);
        }

        if (!Themes.All.Contains(Theme, StringComparer.Ordinal))
        {
            yield return new ValidationResult($"Theme must be one of: {string.Join(", ", Themes.All)}.", [nameof(Theme)]);
        }
    }

    [GeneratedRegex("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();
}

public sealed record ProfileResponse(string? DisplayName, string TimeZone, string Locale, string Theme);

/// <summary>The signed-in user, with what they may do, so a frontend can shape its UI.</summary>
public sealed record MeResponse(string Id, string? Email, ProfileResponse Profile, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

internal static class ProfileEndpoints
{
    /// <summary>The signed-in user's own account. User sign-in only: API keys cannot read or change it.</summary>
    public static RouteGroupBuilder MapProfileEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me").WithTags("Me").RequireAuthorization();

        me.MapGet("/", GetMeAsync).WithSummary("Who you are, your profile and what you may do");
        me.MapPut("/profile", UpdateProfileAsync).WithSummary("Replace your profile and preferences");

        return me;
    }

    private static async Task<MeResponse> GetMeAsync(ClaimsPrincipal user, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken)
            ?? new UserProfile { UserId = userId };

        return new MeResponse(
            userId,
            user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email"),
            ToResponse(profile),
            [.. user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. user.FindAll(Permissions.ClaimType).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private static async Task<ProfileResponse> UpdateProfileAsync(
        UpdateProfileRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId };
            db.UserProfiles.Add(profile);
        }

        profile.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        profile.TimeZone = request.TimeZone;
        profile.Locale = request.Locale;
        profile.Theme = request.Theme;
        profile.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return ToResponse(profile);
    }

    private static ProfileResponse ToResponse(UserProfile profile) =>
        new(profile.DisplayName, profile.TimeZone, profile.Locale, profile.Theme);
}
