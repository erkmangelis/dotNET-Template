namespace MyProject.Api.Features.Profiles;

/// <summary>What a user tells the app about themselves. One row per user, created on first save.</summary>
internal sealed class UserProfile
{
    public const int DisplayNameMaxLength = 100;

    public required string UserId { get; init; }

    public string? DisplayName { get; set; }

    /// <summary>An IANA time zone such as <c>Europe/Istanbul</c>.</summary>
    public string TimeZone { get; set; } = Defaults.TimeZone;

    /// <summary>A BCP 47 language tag such as <c>tr-TR</c>.</summary>
    public string Locale { get; set; } = Defaults.Locale;

    public string Theme { get; set; } = Themes.System;

    public DateTimeOffset UpdatedAt { get; set; }

    public static class Defaults
    {
        public const string TimeZone = "UTC";
        public const string Locale = "en";
    }
}

internal static class Themes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static readonly IReadOnlyList<string> All = [System, Light, Dark];
}
