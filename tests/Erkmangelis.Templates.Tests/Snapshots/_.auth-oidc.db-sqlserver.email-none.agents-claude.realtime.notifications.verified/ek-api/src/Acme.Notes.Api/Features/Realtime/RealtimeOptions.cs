using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Realtime;

/// <summary>Real-time channel settings, bound from the <c>Realtime</c> section.</summary>
internal sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>How long a WebSocket client has to send its <c>hello</c> before the connection is closed.</summary>
    [Range(1, 60)]
    public int HandshakeTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How often an idle connection gets a keep-alive (a WebSocket ping, an SSE <c>heartbeat</c>, a SignalR
    /// ping), so proxies that close quiet connections leave it open.
    /// </summary>
    [Range(5, 300)]
    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>The largest message a client may send, in bytes.</summary>
    [Range(1024, 1024 * 1024)]
    public int MaxMessageBytes { get; set; } = 16 * 1024;

    /// <summary>Messages queued for one slow client. When it falls further behind, its connection is closed and it reconnects.</summary>
    [Range(16, 100_000)]
    public int MaxQueuedMessages { get; set; } = 256;
}
