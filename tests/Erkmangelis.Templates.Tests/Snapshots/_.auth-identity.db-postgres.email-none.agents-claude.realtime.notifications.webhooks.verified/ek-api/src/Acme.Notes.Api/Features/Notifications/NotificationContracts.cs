using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Notifications;

public sealed record NotificationResponse(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string? Link,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt)
{
    public bool Read => ReadAt is not null;

    internal static NotificationResponse From(Notification notification) => new(
        notification.Id,
        notification.Type,
        notification.Title,
        notification.Body,
        notification.Link,
        notification.CreatedAt,
        notification.ReadAt);
}

public sealed record UnreadCountResponse(int Count);

/// <summary>
/// A user's settings for one notification type: each channel outside the app (<c>email</c>, <c>push</c>,
/// <c>sms</c>, as set up here) with whether it is on. In-app is always on.
/// </summary>
public sealed record NotificationPreferenceResponse(string Type, IReadOnlyDictionary<string, bool> Channels);

public sealed record UpdateNotificationPreferenceRequest
{
    /// <summary>Channels to turn on or off, such as <c>{"push": false}</c>. Channels left out keep their setting.</summary>
    [Required]
    public Dictionary<string, bool> Channels { get; init; } = [];
}

public sealed record RegisterPushDeviceRequest
{
    /// <summary><c>fcm</c> (Android, web, or iOS through Firebase) or <c>apns</c> (iOS, directly).</summary>
    [Required]
    public string Platform { get; init; } = string.Empty;

    /// <summary>The token the platform gave this install.</summary>
    [Required]
    [StringLength(PushDevice.TokenMaxLength, MinimumLength = 8)]
    public string Token { get; init; } = string.Empty;
}

public sealed record PushDeviceResponse(Guid Id, string Platform, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt)
{
    internal static PushDeviceResponse From(PushDevice device) =>
        new(device.Id, device.Platform.ToString().ToLowerInvariant(), device.CreatedAt, device.LastSeenAt);
}

public sealed record NotificationContactResponse(string? PhoneNumber);

public sealed record UpdateNotificationContactRequest
{
    /// <summary>The number for text messages in E.164 form, such as <c>+905551234567</c>; null removes it.</summary>
    [RegularExpression(NotificationEndpoints.E164Pattern, ErrorMessage = "Use E.164 form: + then country code and number, such as +905551234567.")]
    public string? PhoneNumber { get; init; }
}

public sealed record SendNotificationRequest
{
    /// <summary>Who receives it: user ids.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(1000)]
    public string[] UserIds { get; init; } = [];

    [Required]
    [StringLength(Notification.TitleMaxLength, MinimumLength = 1)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [StringLength(Notification.BodyMaxLength, MinimumLength = 1)]
    public string Body { get; init; } = string.Empty;

    /// <summary>A path in the app (<c>/notes/…</c>) or an https URL.</summary>
    [StringLength(Notification.LinkMaxLength)]
    public string? Link { get; init; }
}

public sealed record SendNotificationResponse(IReadOnlyList<Guid> NotificationIds);
