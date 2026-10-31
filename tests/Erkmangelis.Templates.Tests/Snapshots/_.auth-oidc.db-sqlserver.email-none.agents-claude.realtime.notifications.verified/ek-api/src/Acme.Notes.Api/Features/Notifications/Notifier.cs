using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Notifications;

/// <summary>
/// The kinds of notification the app sends, each with the channels it uses outside the app until the
/// user says otherwise. Add a type here before sending it; users set preferences per type.
/// </summary>
internal static class NotificationTypes
{
    /// <summary>A message from the team, sent through <c>POST /v1/admin/notifications</c>.</summary>
    public const string Announcement = "announcement";

    public static readonly IReadOnlyDictionary<string, NotificationChannel[]> DefaultChannels = new Dictionary<string, NotificationChannel[]>(StringComparer.Ordinal)
    {
        [Announcement] = [NotificationChannel.Push],
    };
}

/// <summary>Sends a notification to a user: in the app at once, and on their other channels in the background.</summary>
internal interface INotifier
{
    Task<Guid> NotifyAsync(string userId, string type, string title, string body, string? link, CancellationToken cancellationToken);
}

internal sealed class Notifier(
    NotificationsDbContext db,
    NotificationChannels channels,
    IRealtimePublisher realtime,
    TimeProvider time) : INotifier
{
    public const string RealtimeEvent = "notification.created";

    public async Task<Guid> NotifyAsync(string userId, string type, string title, string body, string? link, CancellationToken cancellationToken)
    {
        if (!NotificationTypes.DefaultChannels.TryGetValue(type, out var defaults))
        {
            throw new ArgumentException($"Unknown notification type '{type}'. Add it to {nameof(NotificationTypes)}.", nameof(type));
        }

        var now = time.GetUtcNow();
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Type = type,
            Title = title,
            Body = body,
            Link = link,
            CreatedAt = now,
        };
        db.Notifications.Add(notification);

        var chosen = await db.Preferences
            .Where(preference => preference.UserId == userId && preference.Type == type)
            .ToDictionaryAsync(preference => preference.Channel, preference => preference.Enabled, cancellationToken);
        foreach (var channel in channels.Available.Where(channel => chosen.TryGetValue(channel, out var enabled) ? enabled : defaults.Contains(channel)))
        {
            if (channel == NotificationChannel.Push)
            {
                // One delivery per device, so one failing device never holds up the others.
                var devices = await db.Devices.Where(device => device.UserId == userId).Select(device => device.Id).ToListAsync(cancellationToken);
                foreach (var device in devices)
                {
                    db.Deliveries.Add(NewDelivery(notification, channel, device, now));
                }
            }
            else
            {
                db.Deliveries.Add(NewDelivery(notification, channel, deviceId: null, now));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishAsync(userId, RealtimeEvent, NotificationResponse.From(notification), cancellationToken);
        return notification.Id;
    }

    private static NotificationDelivery NewDelivery(Notification notification, NotificationChannel channel, Guid? deviceId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        NotificationId = notification.Id,
        Channel = channel,
        DeviceId = deviceId,
        Status = OutboxStatus.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };
}

/// <summary>The channels outside the app that are set up here: push and SMS only when a transport is configured.</summary>
internal sealed class NotificationChannels(IOptions<NotificationsOptions> options)
{
    public IReadOnlyList<NotificationChannel> Available { get; } =
    [
        .. options.Value.Push.Transport == PushTransport.None ? Array.Empty<NotificationChannel>() : [NotificationChannel.Push],
        .. options.Value.Sms.Transport == SmsTransport.None ? Array.Empty<NotificationChannel>() : [NotificationChannel.Sms],
    ];
}
