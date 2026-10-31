using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acme.Notes.Api.Features.Realtime;

/// <summary>
/// The raw WebSocket protocol. Every message is one JSON object in one text frame, with a
/// <c>type</c>. The client speaks first:
/// <code>
/// client: {"type":"hello","versions":[1]}
/// server: {"type":"welcome","version":1,"connectionId":"…"}
/// server: {"type":"event","id":"…","event":"note.created","occurredAt":"…","data":{…}}
/// client: {"type":"ping"}             server: {"type":"pong"}
/// </code>
/// The server picks the highest version both sides know. A client that offers none it knows gets an
/// <c>error</c> and a close with 1008 (policy violation); a frame that is not JSON closes with 1007;
/// one over <c>Realtime:MaxMessageBytes</c> closes with 1009.
/// </summary>
internal sealed partial class WebSocketSession(
    WebSocket socket,
    RealtimeConnections.Subscription subscription,
    RealtimeOptions options,
    TimeProvider time,
    ILogger<WebSocketSession> logger) : IDisposable
{
    /// <summary>Protocol versions this server speaks. Add a version here before changing any frame.</summary>
    public static readonly int[] SupportedVersions = [1];

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (await HandshakeAsync(session.Token) is not { } version)
            {
                return;
            }

            LogConnected(logger, subscription.Id, version);
            var sending = SendEventsAsync(session.Token);
            await ReceiveAsync(session.Token);
            await session.CancelAsync();
            await sending;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || session.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            LogClosedUnexpectedly(logger, subscription.Id, exception.Message);
        }
    }

    public void Dispose() => _sendLock.Dispose();

    /// <summary>Reads the client's <c>hello</c> and answers <c>welcome</c>. Null when the connection was closed instead.</summary>
    private async Task<int?> HandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.HandshakeTimeoutSeconds));
        ClientFrame? hello;
        try
        {
            hello = await ReadFrameAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "No hello within the handshake timeout.", cancellationToken);
            return null;
        }

        if (hello is null)
        {
            return null;
        }

        if (hello.Type != "hello")
        {
            await SendAsync(new ErrorFrame("hello_expected", "Send a hello frame with your protocol versions first."), cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "Hello expected.", cancellationToken);
            return null;
        }

        var version = (hello.Versions ?? []).Intersect(SupportedVersions).DefaultIfEmpty().Max();
        if (version == 0)
        {
            await SendAsync(new ErrorFrame("unsupported_version", $"Supported versions: {string.Join(", ", SupportedVersions)}.") { Supported = SupportedVersions }, cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "No supported protocol version.", cancellationToken);
            return null;
        }

        await SendAsync(new WelcomeFrame(version, subscription.Id.ToString()), cancellationToken);
        return version;
    }

    private async Task SendEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await SendAsync(new EventFrame(message.Id, message.Type, message.OccurredAt, message.Data), cancellationToken);
            }
        }
        catch (SlowClientException)
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "Too far behind; reconnect.", cancellationToken);
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        while (await ReadFrameAsync(cancellationToken) is { } frame)
        {
            switch (frame.Type)
            {
                case "ping":
                    await SendAsync(new PongFrame(time.GetUtcNow()), cancellationToken);
                    break;
                default:
                    await SendAsync(new ErrorFrame("unknown_type", $"Frames of type '{frame.Type}' are not understood."), cancellationToken);
                    break;
            }
        }
    }

    /// <summary>The next frame, or null once the connection is closing (a close frame, or a bad frame this method closed on).</summary>
    private async Task<ClientFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[options.MaxMessageBytes + 1];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (length == buffer.Length)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, $"Messages are limited to {options.MaxMessageBytes} bytes.", cancellationToken);
                return null;
            }

            result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            length += result.Count;
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
            return null;
        }

        if (length > options.MaxMessageBytes)
        {
            await CloseAsync(WebSocketCloseStatus.MessageTooBig, $"Messages are limited to {options.MaxMessageBytes} bytes.", cancellationToken);
            return null;
        }

        try
        {
            if (result.MessageType == WebSocketMessageType.Text
                && JsonSerializer.Deserialize<ClientFrame>(buffer.AsSpan(0, length), _json) is { Type: not null } frame)
            {
                return frame;
            }
        }
        catch (JsonException)
        {
        }

        await CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Frames are JSON objects with a type, in text messages.", cancellationToken);
        return null;
    }

    private async Task SendAsync(object frame, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), _json);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string? reason, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(status, reason, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebSocket {ConnectionId} connected with protocol version {Version}")]
    private static partial void LogConnected(ILogger logger, Guid connectionId, int version);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebSocket {ConnectionId} closed without a close handshake: {Reason}")]
    private static partial void LogClosedUnexpectedly(ILogger logger, Guid connectionId, string reason);

    private sealed record ClientFrame(string? Type, int[]? Versions);

    private sealed record WelcomeFrame(int Version, string ConnectionId)
    {
        [JsonPropertyOrder(-1)]
        public string Type { get; } = "welcome";
    }

    private sealed record EventFrame(string Id, string Event, DateTimeOffset OccurredAt, object Data)
    {
        [JsonPropertyOrder(-1)]
        public string Type { get; } = "event";
    }

    private sealed record PongFrame(DateTimeOffset ServerTime)
    {
        [JsonPropertyOrder(-1)]
        public string Type { get; } = "pong";
    }

    private sealed record ErrorFrame(string Code, string Message)
    {
        [JsonPropertyOrder(-1)]
        public string Type { get; } = "error";

        public int[]? Supported { get; init; }
    }
}
