namespace Acme.Notes.Api.Common;

/// <summary>Event types the app publishes. Clients and webhook subscribers see these names.</summary>
internal static class AppEventTypes
{
    public const string NoteCreated = "note.created";
    public const string NoteUpdated = "note.updated";
    public const string NoteDeleted = "note.deleted";

    public static readonly string[] All = [NoteCreated, NoteUpdated, NoteDeleted];
}

/// <summary>
/// Something that happened, for webhook subscribers.
/// </summary>
/// <param name="Type">One of <see cref="AppEventTypes"/>.</param>
/// <param name="UserId">The user it belongs to, or null when it is for everyone.</param>
/// <param name="Data">The payload, serialized as JSON. Use a response record, never an entity.</param>
internal sealed record AppEvent(string Type, string? UserId, object Data);

/// <summary>One listener. Each feature that reacts to events registers one.</summary>
internal interface IAppEventSink
{
    Task HandleAsync(AppEvent appEvent, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}

/// <summary>
/// Hands an event to every <see cref="IAppEventSink"/>. Publish after the change is saved: a listener
/// that fails is logged and the others still run, so a failed listener never undoes the change.
/// </summary>
internal sealed partial class AppEvents(IEnumerable<IAppEventSink> sinks, TimeProvider time, ILogger<AppEvents> logger)
{
    public async Task PublishAsync(AppEvent appEvent, CancellationToken cancellationToken)
    {
        var occurredAt = time.GetUtcNow();
        foreach (var sink in sinks)
        {
            try
            {
                await sink.HandleAsync(appEvent, occurredAt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSinkFailed(logger, appEvent.Type, sink.GetType().Name, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Event {EventType} was not handed to {Sink}")]
    private static partial void LogSinkFailed(ILogger logger, string eventType, string sink, Exception exception);
}
