using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Webhooks;

/// <summary>Turns each published event into a delivery for every enabled subscription that wants it.</summary>
internal sealed class WebhookEventSink(WebhooksDbContext db) : IAppEventSink
{
    public async Task HandleAsync(AppEvent appEvent, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var subscriptions = await db.Subscriptions
            .Where(subscription => subscription.Enabled && subscription.EventTypes.Contains(appEvent.Type))
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = WebhookPayload.Serialize(appEvent.Type, occurredAt, appEvent.Data);
        foreach (var subscriptionId in subscriptions)
        {
            db.Deliveries.Add(WebhookPayload.NewDelivery(subscriptionId, appEvent.Type, payload, occurredAt));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class WebhookPayload
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The body, in the Standard Webhooks shape: <c>{"type": …, "timestamp": …, "data": {…}}</c>.</summary>
    public static string Serialize(string type, DateTimeOffset timestamp, object data) =>
        JsonSerializer.Serialize(new { type, timestamp, data }, _json);

    public static WebhookDelivery NewDelivery(Guid subscriptionId, string type, string payload, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        SubscriptionId = subscriptionId,
        EventType = type,
        Payload = payload,
        Status = OutboxStatus.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };
}

/// <summary>
/// Sends due deliveries: signed POSTs, one attempt each run, with the retry schedule from
/// <c>Webhooks:Outgoing:RetryDelaysSeconds</c>. Subscribers should answer 2xx quickly and drop
/// <c>webhook-id</c>s they have seen, because a delivery can arrive more than once.
/// </summary>
internal sealed partial class WebhookDispatcher(
    WebhooksDbContext db,
    IHttpClientFactory clients,
    IOptions<WebhooksOptions> options,
    TimeProvider time,
    ILogger<WebhookDispatcher> logger)
{
    /// <summary>The HTTP client deliveries go out on: no host list (subscribers choose the URL), but public networks only.</summary>
    public const string ClientName = "webhooks";

    private const int BatchSize = 50;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Outgoing;
        var lease = TimeSpan.FromSeconds(settings.TimeoutSeconds * 2);
        var deliveries = await Outbox.ClaimDueAsync(db.Deliveries, time.GetUtcNow(), lease, BatchSize, cancellationToken);
        foreach (var delivery in deliveries)
        {
            await SendAsync(delivery, settings, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await RemoveOldAsync(settings, cancellationToken);
    }

    private async Task SendAsync(WebhookDelivery delivery, OutgoingWebhooksOptions settings, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.FindAsync([delivery.SubscriptionId], cancellationToken);
        if (subscription is not { Enabled: true })
        {
            Outbox.GiveUp(delivery, "The subscription is disabled.");
            return;
        }

        var now = time.GetUtcNow();
        var body = Encoding.UTF8.GetBytes(delivery.Payload);
        var timestamp = now.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(StandardWebhooks.IdHeader, delivery.Id.ToString());
        request.Headers.Add(StandardWebhooks.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(StandardWebhooks.SignatureHeader, StandardWebhooks.Sign(subscription.Secret, delivery.Id.ToString(), timestamp, body));

        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            delivery.LastStatusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                Outbox.Succeeded(delivery);
                delivery.DeliveredAt = time.GetUtcNow();
            }
            else if (response.StatusCode == HttpStatusCode.Gone)
            {
                // The subscriber says the endpoint is gone for good: stop sending to it.
                subscription.Enabled = false;
                subscription.DisabledReason = "The subscriber answered 410 Gone.";
                subscription.UpdatedAt = now;
                Outbox.GiveUp(delivery, "410 Gone; the subscription was disabled.");
                LogSubscriptionDisabled(logger, subscription.Id);
            }
            else
            {
                Outbox.Failed(delivery, $"The subscriber answered {(int)response.StatusCode}.", now, settings.RetryDelaysSeconds);
            }
        }
        catch (HttpRequestException exception) when ((exception as OutboundRequestRefusedException ?? exception.InnerException) is OutboundRequestRefusedException refused)
        {
            // Retrying cannot make a private address public.
            Outbox.GiveUp(delivery, refused.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            delivery.LastStatusCode = null;
            Outbox.Failed(delivery, exception is TaskCanceledException ? "The subscriber did not answer in time." : exception.Message, now, settings.RetryDelaysSeconds);
        }

        if (delivery.Status == OutboxStatus.Failed)
        {
            LogDeliveryFailed(logger, delivery.Id, subscription.Id, delivery.Attempts, delivery.LastError);
        }
    }

    private async Task RemoveOldAsync(OutgoingWebhooksOptions settings, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-settings.RetentionDays);
        await db.Deliveries
            .Where(delivery => delivery.Status != OutboxStatus.Pending && delivery.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} to subscription {SubscriptionId} failed after {Attempts} attempts: {Error}")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid deliveryId, Guid subscriptionId, int attempts, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook subscription {SubscriptionId} was disabled: the subscriber answered 410 Gone")]
    private static partial void LogSubscriptionDisabled(ILogger logger, Guid subscriptionId);
}

/// <summary>Runs <see cref="WebhookDispatcher"/> every <c>Webhooks:Outgoing:DispatchIntervalSeconds</c>. Instances share the work through the claims.</summary>
internal sealed class WebhookDispatchLoop(IServiceScopeFactory scopes, IOptions<WebhooksOptions> options, TimeProvider time, ILogger<WebhookDispatchLoop> logger)
    : ScopedBackgroundLoop(scopes, time, logger)
{
    protected override string Name => "webhook dispatch";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.Outgoing.DispatchIntervalSeconds);

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<WebhookDispatcher>().RunAsync(cancellationToken);
}
