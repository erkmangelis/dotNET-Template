using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Webhooks;

internal static class WebhookEndpoints
{
    /// <summary>The event type of a test delivery. Every subscription receives it; one can also subscribe to it alone.</summary>
    public const string TestEventType = "webhook.test";

    public static WebApplicationBuilder AddWebhooks(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<WebhooksOptions>()
            .BindConfiguration(WebhooksOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(WebhooksOptions.AreValid, "Every Webhooks:Incoming source needs at least one secret (whsec_ and Base64), and retry delays cannot be negative.")
            .ValidateOnStart();
        ModuleDatabase.Add<WebhooksDbContext>(services, WebhooksDbContext.HistoryTable);

        services.AddScoped<IAppEventSink, WebhookEventSink>();
        services.AddScoped<WebhookDispatcher>();
        services.AddScoped<IncomingWebhookProcessor>();
        services.AddHostedService<WebhookDispatchLoop>();
        services.AddHostedService<IncomingWebhookLoop>();

        // Subscribers choose the URL, so a host list cannot help; the connection is checked instead.
        // No retries here: the delivery schedule retries, with the same webhook-id.
        services.AddHttpClient(WebhookDispatcher.ClientName, (provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<WebhooksOptions>>().Value.Outgoing.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectCallback = provider.GetRequiredService<IOptions<WebhooksOptions>>().Value.Outgoing.AllowPrivateNetworks ? null : PublicNetwork.ConnectAsync,
            });
        return builder;
    }

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        var webhooks = routes.MapGroup("/webhooks").WithTags("Webhooks");

        // Senders prove who they are with the signature, not with a sign-in.
        var receive = webhooks.MapPost("/incoming/{source}", ReceiveAsync)
            .WithName("ReceiveWebhook")
            .WithSummary("Receive a webhook signed the Standard Webhooks way");
        receive.AllowAnonymous();

        var manage = webhooks.MapGroup("").RequirePermission(Permissions.WebhooksManage);
        manage.MapGet("/incoming", ListIncomingAsync).WithName("ListIncomingWebhooks").WithSummary("List received webhooks, newest first");
        manage.MapPost("/incoming/{id:guid}/retry", RetryIncomingAsync).WithName("RetryIncomingWebhook").WithSummary("Hand a failed webhook to its handler again");

        var subscriptions = manage.MapGroup("/subscriptions");
        subscriptions.MapPost("/", CreateAsync).WithName("CreateWebhookSubscription").WithSummary("Subscribe a URL to events; the answer holds the signing secret");
        subscriptions.MapGet("/", ListAsync).WithName("ListWebhookSubscriptions").WithSummary("List webhook subscriptions");
        subscriptions.MapGet("/{id:guid}", GetAsync).WithName("GetWebhookSubscription").WithSummary("Get a webhook subscription");
        subscriptions.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateWebhookSubscription").WithSummary("Change a subscription's URL, events or state");
        subscriptions.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteWebhookSubscription").WithSummary("Delete a subscription and its delivery log");
        subscriptions.MapPost("/{id:guid}/secret", RotateSecretAsync).WithName("RotateWebhookSecret").WithSummary("Replace the signing secret");
        subscriptions.MapPost("/{id:guid}/test", SendTestAsync).WithName("TestWebhookSubscription").WithSummary("Queue a webhook.test delivery");
        subscriptions.MapGet("/{id:guid}/deliveries", ListDeliveriesAsync).WithName("ListWebhookDeliveries").WithSummary("The delivery log, newest first");
        subscriptions.MapPost("/{id:guid}/deliveries/{deliveryId:guid}/retry", RetryDeliveryAsync).WithName("RetryWebhookDelivery").WithSummary("Send a failed delivery again");

        return routes;
    }

    private static async Task<Results<Accepted, Ok, NotFound, ProblemHttpResult>> ReceiveAsync(
        string source,
        HttpRequest request,
        WebhooksDbContext db,
        IOptions<WebhooksOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.Incoming;
        if (!settings.Sources.TryGetValue(source, out var sender))
        {
            return TypedResults.NotFound();
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();
        var now = time.GetUtcNow();
        var messageId = request.Headers[StandardWebhooks.IdHeader].ToString();
        var verification = StandardWebhooks.Verify(
            sender.Secrets,
            messageId,
            request.Headers[StandardWebhooks.TimestampHeader],
            request.Headers[StandardWebhooks.SignatureHeader],
            body,
            now,
            TimeSpan.FromSeconds(settings.ToleranceSeconds));
        if (verification != WebhookVerification.Valid)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Webhook not verified",
                detail: verification switch
                {
                    WebhookVerification.MissingHeaders => "webhook-id, webhook-timestamp and webhook-signature are required.",
                    WebhookVerification.TimestampOutOfRange => "webhook-timestamp is too far from the server's time; old messages are refused as replays.",
                    _ => "The signature does not match the body.",
                });
        }

        if (messageId.Length > IncomingWebhook.MessageIdMaxLength)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "webhook-id too long", detail: $"webhook-id is limited to {IncomingWebhook.MessageIdMaxLength} characters.");
        }

        string? eventType;
        try
        {
            using var document = JsonDocument.Parse(body);
            eventType = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body is not JSON", detail: "Webhook bodies must be JSON.");
        }

        var name = source.ToLowerInvariant();
        // A sender retries until it hears 2xx, so a message already stored is acknowledged, not stored again.
        if (await db.Incoming.AnyAsync(message => message.Source == name && message.MessageId == messageId, cancellationToken))
        {
            return TypedResults.Ok();
        }

        db.Incoming.Add(new IncomingWebhook
        {
            Id = Guid.CreateVersion7(now),
            Source = name,
            MessageId = messageId,
            EventType = eventType is { Length: <= 100 } ? eventType : null,
            Payload = System.Text.Encoding.UTF8.GetString(body),
            Status = OutboxStatus.Pending,
            NextAttemptAt = now,
            ReceivedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            // The same message arrived twice at once; the other request stored it.
            return TypedResults.Ok();
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Ok<PagedResult<IncomingWebhookResponse>>> ListIncomingAsync(
        WebhooksDbContext db,
        CancellationToken cancellationToken,
        string? source = null,
        string? status = null,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        var query = db.Incoming.AsNoTracking();
        if (source is not null)
        {
            var name = source.ToLowerInvariant();
            query = query.Where(message => message.Source == name);
        }

        if (Enum.TryParse<OutboxStatus>(status, ignoreCase: true, out var wanted))
        {
            query = query.Where(message => message.Status == wanted);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(message => message.ReceivedAt)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<IncomingWebhookResponse>([.. items.Select(IncomingWebhookResponse.From)], page, pageSize, total));
    }

    private static async Task<Results<Accepted, NotFound, ProblemHttpResult>> RetryIncomingAsync(
        Guid id,
        WebhooksDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var message = await db.Incoming.FirstOrDefaultAsync(message => message.Id == id, cancellationToken);
        if (message is null)
        {
            return TypedResults.NotFound();
        }

        if (message.Status != OutboxStatus.Failed)
        {
            return NotFailed();
        }

        Outbox.Retry(message, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Created<WebhookSecretResponse>, ValidationProblem>> CreateAsync(
        CreateWebhookSubscriptionRequest request,
        WebhooksDbContext db,
        IOptions<WebhooksOptions> options,
        TimeProvider time,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (Check(request.Url, request.EventTypes, options.Value.Outgoing) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var now = time.GetUtcNow();
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(now),
            Url = request.Url,
            Description = request.Description,
            EventTypes = [.. request.EventTypes.Distinct(StringComparer.Ordinal)],
            Secret = StandardWebhooks.NewSecret(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            ApiVersions.PathFor(http, $"webhooks/subscriptions/{subscription.Id}"),
            new WebhookSecretResponse(WebhookSubscriptionResponse.From(subscription), subscription.Secret));
    }

    private static async Task<Ok<List<WebhookSubscriptionResponse>>> ListAsync(WebhooksDbContext db, CancellationToken cancellationToken)
    {
        var subscriptions = await db.Subscriptions.AsNoTracking().OrderBy(subscription => subscription.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(subscriptions.Select(WebhookSubscriptionResponse.From).ToList());
    }

    private static async Task<Results<Ok<WebhookSubscriptionResponse>, NotFound>> GetAsync(Guid id, WebhooksDbContext db, CancellationToken cancellationToken) =>
        await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(subscription => subscription.Id == id, cancellationToken) is { } subscription
            ? TypedResults.Ok(WebhookSubscriptionResponse.From(subscription))
            : TypedResults.NotFound();

    private static async Task<Results<Ok<WebhookSubscriptionResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        WebhooksDbContext db,
        IOptions<WebhooksOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(subscription => subscription.Id == id, cancellationToken);
        if (subscription is null)
        {
            return TypedResults.NotFound();
        }

        if (Check(request.Url, request.EventTypes, options.Value.Outgoing) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        subscription.Url = request.Url;
        subscription.EventTypes = [.. request.EventTypes.Distinct(StringComparer.Ordinal)];
        subscription.Description = request.Description;
        subscription.Enabled = request.Enabled;
        subscription.DisabledReason = request.Enabled ? null : subscription.DisabledReason;
        subscription.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(WebhookSubscriptionResponse.From(subscription));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, WebhooksDbContext db, CancellationToken cancellationToken) =>
        await db.Subscriptions.Where(subscription => subscription.Id == id).ExecuteDeleteAsync(cancellationToken) == 0
            ? TypedResults.NotFound()
            : TypedResults.NoContent();

    private static async Task<Results<Ok<WebhookSecretResponse>, NotFound>> RotateSecretAsync(
        Guid id,
        WebhooksDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(subscription => subscription.Id == id, cancellationToken);
        if (subscription is null)
        {
            return TypedResults.NotFound();
        }

        // Deliveries sign with the new secret from now on, retries of older ones included.
        subscription.Secret = StandardWebhooks.NewSecret();
        subscription.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new WebhookSecretResponse(WebhookSubscriptionResponse.From(subscription), subscription.Secret));
    }

    private static async Task<Results<Accepted<WebhookDeliveryResponse>, NotFound>> SendTestAsync(
        Guid id,
        WebhooksDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!await db.Subscriptions.AnyAsync(subscription => subscription.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        var delivery = WebhookPayload.NewDelivery(id, TestEventType, WebhookPayload.Serialize(TestEventType, now, new { subscriptionId = id }), now);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted((string?)null, WebhookDeliveryResponse.From(delivery));
    }

    private static async Task<Results<Ok<PagedResult<WebhookDeliveryResponse>>, NotFound>> ListDeliveriesAsync(
        Guid id,
        WebhooksDbContext db,
        CancellationToken cancellationToken,
        string? status = null,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        if (!await db.Subscriptions.AnyAsync(subscription => subscription.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var query = db.Deliveries.AsNoTracking().Where(delivery => delivery.SubscriptionId == id);
        if (Enum.TryParse<OutboxStatus>(status, ignoreCase: true, out var wanted))
        {
            query = query.Where(delivery => delivery.Status == wanted);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(delivery => delivery.CreatedAt)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<WebhookDeliveryResponse>([.. items.Select(WebhookDeliveryResponse.From)], page, pageSize, total));
    }

    private static async Task<Results<Accepted, NotFound, ProblemHttpResult>> RetryDeliveryAsync(
        Guid id,
        Guid deliveryId,
        WebhooksDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.FirstOrDefaultAsync(delivery => delivery.Id == deliveryId && delivery.SubscriptionId == id, cancellationToken);
        if (delivery is null)
        {
            return TypedResults.NotFound();
        }

        if (delivery.Status != OutboxStatus.Failed)
        {
            return NotFailed();
        }

        Outbox.Retry(delivery, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Field errors for a subscription's URL and event types, or null when both are fine.</summary>
    internal static Dictionary<string, string[]>? Check(string url, string[] eventTypes, OutgoingWebhooksOptions options)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (CheckUrl(url, options) is { } urlError)
        {
            errors["Url"] = [urlError];
        }

        string[] known = [.. AppEventTypes.All, TestEventType];
        var unknown = eventTypes.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            errors["EventTypes"] = [$"Unknown event types: {string.Join(", ", unknown)}. Known: {string.Join(", ", known)}."];
        }

        return errors.Count == 0 ? null : errors;
    }

    /// <summary>
    /// The checks a URL can fail before any request: absolute https, no credentials in it, not localhost
    /// and not a private address written out. A name that resolves to one is refused when delivering.
    /// </summary>
    internal static string? CheckUrl(string url, OutgoingWebhooksOptions options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Use an absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(options.AllowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "Use an https URL.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Put no user name or password in the URL; deliveries are signed instead.";
        }

        if (!options.AllowPrivateNetworks
            && (uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var address) && !PublicNetwork.IsPublic(address))))
        {
            return "Use an address on the public internet.";
        }

        return null;
    }

    private static ProblemHttpResult NotFailed() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Not failed",
        detail: "Only a failed item can be retried; pending ones are retried on their own.");
}
