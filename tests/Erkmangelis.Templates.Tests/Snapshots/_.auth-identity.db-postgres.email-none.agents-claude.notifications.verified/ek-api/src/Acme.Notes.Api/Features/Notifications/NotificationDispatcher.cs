using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Notifications;

/// <summary>
/// Sends due deliveries on their channel, one attempt each run, with the retry schedule from
/// <c>Notifications:RetryDelaysSeconds</c>, and removes notifications older than <c>RetentionDays</c>.
/// </summary>
internal sealed partial class NotificationDispatcher(
    NotificationsDbContext db,
    PushRouter push,
    SmsRouter sms,
    IOptions<NotificationsOptions> options,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger)
{
    private const int BatchSize = 50;
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(2);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var deliveries = await Outbox.ClaimDueAsync(db.Deliveries, time.GetUtcNow(), _lease, BatchSize, cancellationToken);
        foreach (var delivery in deliveries)
        {
            await SendAsync(delivery, settings, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var cutoff = time.GetUtcNow().AddDays(-settings.RetentionDays);
        await db.Notifications.Where(notification => notification.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task SendAsync(NotificationDelivery delivery, NotificationsOptions settings, CancellationToken cancellationToken)
    {
        var notification = await db.Notifications.FindAsync([delivery.NotificationId], cancellationToken);
        if (notification is null)
        {
            Outbox.GiveUp(delivery, "The notification was deleted.");
            return;
        }

        try
        {
            var skipped = delivery.Channel switch
            {
                NotificationChannel.Push => await SendPushAsync(delivery, notification, cancellationToken),
                NotificationChannel.Sms => await SendSmsAsync(notification, cancellationToken),
                _ => $"Nothing sends on the {delivery.Channel} channel.",
            };
            if (skipped is null)
            {
                Outbox.Succeeded(delivery);
                delivery.SentAt = time.GetUtcNow();
            }
            else
            {
                Outbox.GiveUp(delivery, skipped);
            }
        }
        catch (NotificationRejectedException exception)
        {
            Outbox.GiveUp(delivery, exception.Message);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Outbox.Failed(delivery, exception.Message, time.GetUtcNow(), settings.RetryDelaysSeconds);
        }

        if (delivery.Status == OutboxStatus.Failed)
        {
            LogDeliveryFailed(logger, delivery.Id, delivery.Channel, delivery.Attempts, delivery.LastError);
        }
    }

    private async Task<string?> SendPushAsync(NotificationDelivery delivery, Notification notification, CancellationToken cancellationToken)
    {
        var device = await db.Devices.FindAsync([delivery.DeviceId], cancellationToken);
        if (device is null || device.UserId != notification.UserId)
        {
            return "The device was removed.";
        }

        var outcome = await push.SendAsync(device, new PushMessage(notification.Id, notification.Type, notification.Title, notification.Body, notification.Link), cancellationToken);
        if (outcome == PushOutcome.InvalidToken)
        {
            // The app was uninstalled or the token rotated: the device will register its new token.
            db.Devices.Remove(device);
            return "The push service no longer knows the device; it was removed.";
        }

        return null;
    }

    private async Task<string?> SendSmsAsync(Notification notification, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.FindAsync([notification.UserId], cancellationToken);
        if (contact?.PhoneNumber is not { } phoneNumber)
        {
            return "The user has no phone number.";
        }

        // A text has no room for formatting: the title, then the link.
        var text = notification.Link is null ? notification.Title : $"{notification.Title} {notification.Link}";
        await sms.SendAsync(phoneNumber, text, cancellationToken);
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification delivery {DeliveryId} by {Channel} failed after {Attempts} attempts: {Error}")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid deliveryId, NotificationChannel channel, int attempts, string? error);
}

/// <summary>Runs <see cref="NotificationDispatcher"/> every <c>Notifications:DispatchIntervalSeconds</c>.</summary>
internal sealed class NotificationDispatchLoop(IServiceScopeFactory scopes, IOptions<NotificationsOptions> options, TimeProvider time, ILogger<NotificationDispatchLoop> logger)
    : ScopedBackgroundLoop(scopes, time, logger)
{
    protected override string Name => "notification dispatch";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.DispatchIntervalSeconds);

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<NotificationDispatcher>().RunAsync(cancellationToken);
}
