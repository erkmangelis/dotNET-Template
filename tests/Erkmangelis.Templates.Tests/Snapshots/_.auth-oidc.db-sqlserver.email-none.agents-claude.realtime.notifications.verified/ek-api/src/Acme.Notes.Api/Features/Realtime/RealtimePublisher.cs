using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Realtime;

/// <summary>
/// What every channel carries: a SignalR <c>event</c> call, an SSE event (named by <see cref="Type"/>,
/// with <see cref="Id"/> as its id) and a WebSocket <c>event</c> frame.
/// </summary>
public sealed record RealtimeMessage(string Id, string Type, DateTimeOffset OccurredAt, object Data);

/// <summary>Sends a message to connected clients, on every channel at once.</summary>
internal interface IRealtimePublisher
{
    /// <summary>Sends to <paramref name="userId"/>'s connections, or to everyone's when it is null.</summary>
    Task PublishAsync(string? userId, string type, object data, CancellationToken cancellationToken);
}

/// <summary>
/// The SignalR hub's clients, plus the server-sent event and WebSocket connections in
/// <see cref="RealtimeConnections"/>. Connections live in this process: with more than one instance,
/// a client only hears what its own instance publishes (see docs/realtime.md for a backplane).
/// </summary>
internal sealed class RealtimePublisher(IHubContext<RealtimeHub> hub, RealtimeConnections connections, TimeProvider time)
    : IRealtimePublisher, IAppEventSink
{
    public Task PublishAsync(string? userId, string type, object data, CancellationToken cancellationToken) =>
        SendAsync(userId, type, data, time.GetUtcNow(), cancellationToken);

    public Task HandleAsync(AppEvent appEvent, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
        SendAsync(appEvent.UserId, appEvent.Type, appEvent.Data, occurredAt, cancellationToken);

    private async Task SendAsync(string? userId, string type, object data, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var message = new RealtimeMessage(Guid.CreateVersion7(occurredAt).ToString(), type, occurredAt, data);
        connections.Publish(userId, message);
        var clients = userId is null ? hub.Clients.All : hub.Clients.User(userId);
        await clients.SendAsync(RealtimeHub.EventMethod, message, cancellationToken);
    }
}

/// <summary>The server-sent event and WebSocket connections open in this process, each with its own bounded queue.</summary>
internal sealed class RealtimeConnections(IOptions<RealtimeOptions> options)
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public int Count => _subscriptions.Count;

    /// <summary>Starts queueing messages for <paramref name="userId"/> (or for everyone's, when null). Dispose it when the connection ends.</summary>
    public Subscription Subscribe(string? userId)
    {
        var subscription = new Subscription(this, userId, options.Value.MaxQueuedMessages);
        _subscriptions[subscription.Id] = subscription;
        return subscription;
    }

    public void Publish(string? userId, RealtimeMessage message)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (userId is null || subscription.UserId == userId)
            {
                subscription.Offer(message);
            }
        }
    }

    internal sealed class Subscription : IDisposable
    {
        private readonly RealtimeConnections _owner;
        private readonly Channel<RealtimeMessage> _queue;

        public Subscription(RealtimeConnections owner, string? userId, int capacity)
        {
            _owner = owner;
            UserId = userId;
            _queue = Channel.CreateBounded<RealtimeMessage>(new BoundedChannelOptions(capacity) { SingleReader = true });
        }

        public Guid Id { get; } = Guid.NewGuid();

        public string? UserId { get; }

        /// <summary>Completes with an error when the client fell too far behind; the connection then ends.</summary>
        public ChannelReader<RealtimeMessage> Reader => _queue.Reader;

        public void Offer(RealtimeMessage message)
        {
            // A client that cannot keep up is cut off rather than slowing everyone down or using unbounded memory.
            if (!_queue.Writer.TryWrite(message))
            {
                _queue.Writer.TryComplete(new SlowClientException());
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            _owner._subscriptions.TryRemove(Id, out _);
        }
    }
}

/// <summary>A client's queue overflowed.</summary>
internal sealed class SlowClientException() : Exception("The client fell too far behind and was disconnected.");
