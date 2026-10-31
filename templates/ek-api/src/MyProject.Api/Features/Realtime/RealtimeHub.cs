using Microsoft.AspNetCore.SignalR;

namespace MyProject.Api.Features.Realtime;

/// <summary>
/// The SignalR hub at <see cref="RealtimeSetup.HubPath"/>. The server calls <see cref="EventMethod"/>
/// on clients with a <see cref="RealtimeMessage"/>; SignalR handles transports (WebSockets, then
/// server-sent events, then long polling), reconnects and its own keep-alives.
/// </summary>
#if (UseAuth)
/// <remarks>
/// Only signed-in users connect, and <c>Clients.User(id)</c> reaches every connection of that user,
/// matched on the <c>NameIdentifier</c> claim.
/// </remarks>
#endif
internal sealed class RealtimeHub(TimeProvider time) : Hub
{
    public const string EventMethod = "event";

    /// <summary>A round trip clients can use to check the connection: returns the server's time.</summary>
    public DateTimeOffset Ping() => time.GetUtcNow();
}
