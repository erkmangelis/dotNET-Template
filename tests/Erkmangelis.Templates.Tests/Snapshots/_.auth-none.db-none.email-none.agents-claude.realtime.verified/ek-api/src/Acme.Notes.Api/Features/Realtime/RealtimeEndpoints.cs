using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Realtime;

internal static class RealtimeEndpoints
{
    /// <summary>The SSE event sent first, once the stream is subscribed: anything published after it arrives.</summary>
    public const string ConnectedEvent = "connected";

    /// <summary>The SSE event sent on a quiet stream every <c>Realtime:KeepAliveSeconds</c>.</summary>
    public const string HeartbeatEvent = "heartbeat";

    /// <summary>Server-sent events at <c>/v1/realtime/events</c> and the WebSocket at <c>/v1/realtime/ws</c>.</summary>
    public static RouteGroupBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder routes)
    {
        var realtime = routes.MapGroup("/realtime").WithTags("Realtime");
        realtime.MapGet("/events", Events)
            .WithName("RealtimeEvents")
            .WithSummary("A server-sent event stream of everything published to you");
        realtime.MapGet("/ws", ConnectAsync)
            .WithName("RealtimeWebSocket")
            .WithSummary("A WebSocket with JSON frames: send hello with your protocol versions first (see docs/realtime.md)");

        return realtime;
    }

    private static Results<ServerSentEventsResult<RealtimeMessage>, ProblemHttpResult> Events(
        HttpContext http,
        RealtimeConnections connections,
        IOptions<RealtimeOptions> options,
        IOptions<CorsSettings> cors,
        TimeProvider time)
    {
        if (!RealtimeOrigins.IsAllowed(http, cors.Value))
        {
            return CrossSiteRefused();
        }

        // Tells proxies such as nginx not to buffer the stream.
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var stream = StreamAsync(connections, userId: null, TimeSpan.FromSeconds(options.Value.KeepAliveSeconds), time, http.RequestAborted);
        return TypedResults.ServerSentEvents(stream);
    }

    private static async IAsyncEnumerable<SseItem<RealtimeMessage>> StreamAsync(
        RealtimeConnections connections,
        string? userId,
        TimeSpan keepAlive,
        TimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Subscribed before the first event, so everything published after "connected" arrives.
        using var subscription = connections.Subscribe(userId);
        yield return Item(new RealtimeMessage(subscription.Id.ToString(), ConnectedEvent, time.GetUtcNow(), new { }));

        while (!cancellationToken.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(keepAlive);
            bool more;
            try
            {
                more = await subscription.Reader.WaitToReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                more = true;
            }

            if (!more)
            {
                yield break;
            }

            var sent = false;
            while (subscription.Reader.TryRead(out var message))
            {
                sent = true;
                yield return Item(message);
            }

            if (!sent)
            {
                yield return Item(new RealtimeMessage(Guid.CreateVersion7(time.GetUtcNow()).ToString(), HeartbeatEvent, time.GetUtcNow(), new { }));
            }
        }
    }

    private static SseItem<RealtimeMessage> Item(RealtimeMessage message) => new(message, message.Type) { EventId = message.Id };

    private static async Task<Results<EmptyHttpResult, ProblemHttpResult>> ConnectAsync(
        HttpContext http,
        RealtimeConnections connections,
        IOptions<RealtimeOptions> options,
        IOptions<CorsSettings> cors,
        TimeProvider time,
        ILogger<WebSocketSession> logger)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "WebSocket expected", detail: "Connect to this endpoint with a WebSocket upgrade request.");
        }

        // Browsers do not apply CORS to WebSockets, so a page on another site could open one with this user's cookies.
        if (!RealtimeOrigins.IsAllowed(http, cors.Value))
        {
            return CrossSiteRefused();
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        using var subscription = connections.Subscribe(userId: null);
        using var session = new WebSocketSession(socket, subscription, options.Value, time, logger);
        await session.RunAsync(http.RequestAborted);
        return TypedResults.Empty;
    }

    private static ProblemHttpResult CrossSiteRefused() => TypedResults.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Origin not allowed",
        detail: "Pages on this origin may not open real-time connections. Add it to Cors:AllowedOrigins if they should.");
}

/// <summary>Which pages may open a real-time connection from a browser.</summary>
internal static class RealtimeOrigins
{
    /// <summary>
    /// True without an Origin header (not a browser), for the API's own origin, and for the origins in
    /// <c>Cors:AllowedOrigins</c>. Anything else is a page on another site.
    /// </summary>
    public static bool IsAllowed(HttpContext http, CorsSettings cors)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        var own = $"{http.Request.Scheme}://{http.Request.Host}";
        return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase)
            || cors.AllowedOrigins.Any(allowed => string.Equals(origin, allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}
