using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Features.Webhooks;

/// <summary>A verified webhook, as a handler receives it.</summary>
/// <param name="Id">This app's id for the message.</param>
/// <param name="Source">The source name from the URL, in lower case.</param>
/// <param name="MessageId">The sender's <c>webhook-id</c>. Use it to make the handler safe to run twice.</param>
/// <param name="EventType">The body's <c>type</c>, when it has one.</param>
/// <param name="Payload">The body.</param>
/// <param name="ReceivedAt">When this app accepted it.</param>
public sealed record IncomingWebhookMessage(Guid Id, string Source, string MessageId, string? EventType, JsonElement Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// Handles the webhooks of one source. Register it with
/// <see cref="IncomingWebhookHandlers.AddIncomingWebhookHandler{THandler}"/>. It runs in the background,
/// after the sender already got its answer; throwing schedules a retry, so make it safe to repeat.
/// </summary>
internal interface IIncomingWebhookHandler
{
    Task HandleAsync(IncomingWebhookMessage message, CancellationToken cancellationToken);
}

internal static class IncomingWebhookHandlers
{
    /// <summary>Handles the webhooks that arrive at <c>/v1/webhooks/incoming/{source}</c> with <typeparamref name="THandler"/>.</summary>
    public static IServiceCollection AddIncomingWebhookHandler<THandler>(this IServiceCollection services, string source)
        where THandler : class, IIncomingWebhookHandler
    {
        services.AddKeyedScoped<IIncomingWebhookHandler, THandler>(source.ToLowerInvariant());
        return services;
    }
}

/// <summary>Hands stored webhooks to their source's handler, with retries, and removes old ones.</summary>
internal sealed partial class IncomingWebhookProcessor(
    WebhooksDbContext db,
    IServiceScopeFactory scopes,
    IOptions<WebhooksOptions> options,
    TimeProvider time,
    ILogger<IncomingWebhookProcessor> logger)
{
    private const int BatchSize = 50;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Incoming;
        var messages = await Outbox.ClaimDueAsync(db.Incoming, time.GetUtcNow(), TimeSpan.FromMinutes(5), BatchSize, cancellationToken);
        foreach (var message in messages)
        {
            await HandleAsync(message, settings, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var cutoff = time.GetUtcNow().AddDays(-settings.RetentionDays);
        await db.Incoming.Where(message => message.Status != OutboxStatus.Pending && message.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task HandleAsync(IncomingWebhook message, IncomingWebhooksOptions settings, CancellationToken cancellationToken)
    {
        // Each message gets its own scope, so one handler's failed work never leaks into the next.
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetKeyedService<IIncomingWebhookHandler>(message.Source);
        if (handler is null)
        {
            Outbox.GiveUp(message, $"No handler is registered for source '{message.Source}'.");
            LogNoHandler(logger, message.Source, message.Id);
            return;
        }

        try
        {
            using var payload = JsonDocument.Parse(message.Payload);
            await handler.HandleAsync(
                new IncomingWebhookMessage(message.Id, message.Source, message.MessageId, message.EventType, payload.RootElement.Clone(), message.ReceivedAt),
                cancellationToken);
            Outbox.Succeeded(message);
            message.ProcessedAt = time.GetUtcNow();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Outbox.Failed(message, $"{exception.GetType().Name}: {exception.Message}", time.GetUtcNow(), settings.RetryDelaysSeconds);
            LogHandlerFailed(logger, message.Source, message.Id, message.Attempts, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {MessageId} from {Source} has no handler")]
    private static partial void LogNoHandler(ILogger logger, string source, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling webhook {MessageId} from {Source} failed (attempt {Attempts})")]
    private static partial void LogHandlerFailed(ILogger logger, string source, Guid messageId, int attempts, Exception exception);
}

/// <summary>Runs <see cref="IncomingWebhookProcessor"/> every <c>Webhooks:Incoming:ProcessIntervalSeconds</c>.</summary>
internal sealed class IncomingWebhookLoop(IServiceScopeFactory scopes, IOptions<WebhooksOptions> options, TimeProvider time, ILogger<IncomingWebhookLoop> logger)
    : ScopedBackgroundLoop(scopes, time, logger)
{
    protected override string Name => "incoming webhooks";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.Incoming.ProcessIntervalSeconds);

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<IncomingWebhookProcessor>().RunAsync(cancellationToken);
}
