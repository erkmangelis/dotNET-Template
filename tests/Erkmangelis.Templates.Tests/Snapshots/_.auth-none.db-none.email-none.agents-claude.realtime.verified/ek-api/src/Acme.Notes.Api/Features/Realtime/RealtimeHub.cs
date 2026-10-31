using Microsoft.AspNetCore.SignalR;

namespace Acme.Notes.Api.Features.Realtime;

/// <summary>
/// The SignalR hub at <see cref="RealtimeSetup.HubPath"/>. The server calls <see cref="EventMethod"/>
/// on clients with a <see cref="RealtimeMessage"/>; SignalR handles transports (WebSockets, then
/// server-sent events, then long polling), reconnects and its own keep-alives.
/// </summary>
internal sealed class RealtimeHub(TimeProvider time) : Hub
{
    public const string EventMethod = "event";

    /// <summary>A round trip clients can use to check the connection: returns the server's time.</summary>
    public DateTimeOffset Ping() => time.GetUtcNow();
}
