using System.ComponentModel.DataAnnotations;

namespace MyProject.Api.Features.Webhooks;

/// <summary>Webhook settings, bound from the <c>Webhooks</c> section.</summary>
internal sealed class WebhooksOptions
{
    public const string SectionName = "Webhooks";

    public IncomingWebhooksOptions Incoming { get; set; } = new();

    public OutgoingWebhooksOptions Outgoing { get; set; } = new();

    public static bool AreValid(WebhooksOptions options) =>
        options.Incoming.Sources.Values.All(source => source.Secrets.Length > 0 && source.Secrets.All(StandardWebhooks.IsValidSecret))
        && options.Incoming.RetryDelaysSeconds.All(delay => delay >= 0)
        && options.Outgoing.RetryDelaysSeconds.All(delay => delay >= 0);
}

/// <summary>Webhooks other systems send to <c>POST /v1/webhooks/incoming/{source}</c>.</summary>
internal sealed class IncomingWebhooksOptions
{
    /// <summary>
    /// The senders this API accepts, by the name in the URL. Each needs a secret (<c>whsec_…</c>) that the
    /// sender signs with; list two while rotating. Keep secrets in user secrets or environment variables.
    /// </summary>
    public Dictionary<string, IncomingWebhookSource> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far a message's timestamp may be from the server's clock. Older messages are refused as replays.</summary>
    [Range(30, 3600)]
    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>Waits before each retry of a handler that failed. One retry per entry; then the message is marked failed.</summary>
    public int[] RetryDelaysSeconds { get; set; } = [];

    [Range(1, 3600)]
    public int ProcessIntervalSeconds { get; set; } = 5;

    /// <summary>How long handled messages are kept. Duplicates are recognised for as long as the message is kept.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;
}

internal sealed class IncomingWebhookSource
{
    public string[] Secrets { get; set; } = [];
}

/// <summary>Webhooks this API sends to its subscribers.</summary>
internal sealed class OutgoingWebhooksOptions
{
    /// <summary>Waits before each retry of a failed delivery. One retry per entry; then the delivery is marked failed.</summary>
    public int[] RetryDelaysSeconds { get; set; } = [];

    [Range(1, 3600)]
    public int DispatchIntervalSeconds { get; set; } = 5;

    /// <summary>How long a subscriber has to answer one delivery.</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Deliver to private and reserved networks (localhost, 10.x, the cloud metadata address). Only for
    /// development: subscribers choose the URL, and this is what stops them aiming it at your own network.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Allow http subscription URLs. Only for development.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>How long finished deliveries stay in the delivery log.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;
}
