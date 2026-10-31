using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Webhooks;

public sealed record CreateWebhookSubscriptionRequest
{
    /// <summary>Where deliveries are POSTed: an https URL on the public internet.</summary>
    [Required]
    [StringLength(WebhookSubscription.UrlMaxLength)]
    public string Url { get; init; } = string.Empty;

    /// <summary>The event types to deliver, such as <c>note.created</c>.</summary>
    [Required]
    [MinLength(1)]
    public string[] EventTypes { get; init; } = [];

    [StringLength(WebhookSubscription.DescriptionMaxLength)]
    public string? Description { get; init; }
}

public sealed record UpdateWebhookSubscriptionRequest
{
    [Required]
    [StringLength(WebhookSubscription.UrlMaxLength)]
    public string Url { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string[] EventTypes { get; init; } = [];

    [StringLength(WebhookSubscription.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Turn delivery on or off. Turning it on again clears the reason the app turned it off.</summary>
    public bool Enabled { get; init; } = true;
}

public sealed record WebhookSubscriptionResponse(
    Guid Id,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool Enabled,
    string? DisabledReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static WebhookSubscriptionResponse From(WebhookSubscription subscription) => new(
        subscription.Id,
        subscription.Url,
        subscription.Description,
        subscription.EventTypes,
        subscription.Enabled,
        subscription.DisabledReason,
        subscription.CreatedAt,
        subscription.UpdatedAt);
}

/// <summary>A subscription with its signing secret. The secret is only ever shown here: store it now.</summary>
public sealed record WebhookSecretResponse(WebhookSubscriptionResponse Subscription, string Secret);

/// <summary>One delivery in the log. <c>status</c> is <c>pending</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record WebhookDeliveryResponse(
    Guid Id,
    string EventType,
    string Status,
    int Attempts,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? DeliveredAt)
{
    internal static WebhookDeliveryResponse From(WebhookDelivery delivery) => new(
        delivery.Id,
        delivery.EventType,
        delivery.Status.ToString().ToLowerInvariant(),
        delivery.Attempts,
        delivery.LastStatusCode,
        delivery.LastError,
        delivery.CreatedAt,
        delivery.Status == OutboxStatus.Pending ? delivery.NextAttemptAt : null,
        delivery.DeliveredAt);
}

/// <summary>A received webhook. <c>status</c> is <c>pending</c>, <c>succeeded</c> (handled) or <c>failed</c>.</summary>
public sealed record IncomingWebhookResponse(
    Guid Id,
    string Source,
    string MessageId,
    string? EventType,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt)
{
    internal static IncomingWebhookResponse From(IncomingWebhook message) => new(
        message.Id,
        message.Source,
        message.MessageId,
        message.EventType,
        message.Status.ToString().ToLowerInvariant(),
        message.Attempts,
        message.LastError,
        message.ReceivedAt,
        message.ProcessedAt);
}
