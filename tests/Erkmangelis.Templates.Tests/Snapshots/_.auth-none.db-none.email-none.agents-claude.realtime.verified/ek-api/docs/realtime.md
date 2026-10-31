# Real-time

Connected clients hear about changes as they happen, on three channels. All three carry the same
events; pick the one that suits the client.

| Channel | Route | Use it for |
| --- | --- | --- |
| SignalR | `/v1/realtime/hub` | Apps with a SignalR client (JavaScript, .NET, Java, Swift); it reconnects and falls back for you |
| Server-sent events | `GET /v1/realtime/events` | Browsers and scripts that only listen: plain HTTP, `EventSource` in the browser |
| WebSocket | `/v1/realtime/ws` | Clients without SignalR that also want to send (a game, a device, another language) |

Every event has the same shape:

```json
{ "id": "…", "type": "note.created", "occurredAt": "2026-10-31T09:00:00Z", "data": { … } }
```

Every connected client hears every event: there are no users to tell apart.

## Publishing an event

Endpoints publish through `AppEvents` after the change is saved, as the notes endpoints do:

```csharp
await events.PublishAsync(new AppEvent(AppEventTypes.NoteCreated, userId, NoteResponse.From(note)), cancellationToken);
```

Add the type to `AppEventTypes` first. `AppEvents` hands the event to every `IAppEventSink`; the
real-time publisher is one of them. To send to one user directly, inject `IRealtimePublisher`.
A failing sink is logged and never fails the request.

Events are best effort: a client that is offline misses them. Clients should load the current state
when they connect and use events to stay up to date, not as the only record of a change.

## SignalR

```js
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/v1/realtime/hub")
  .withAutomaticReconnect()
  .build();
connection.on("event", message => console.log(message.type, message.data));
await connection.start();
```

## Server-sent events

```js
const events = new EventSource("/v1/realtime/events");
events.addEventListener("note.created", event => console.log(JSON.parse(event.data)));
```

The stream starts with a `connected` event and sends a `heartbeat` event when it has been quiet for
`Realtime:KeepAliveSeconds`, so proxies do not close it. The SSE event name is the event type.

## WebSocket protocol

Messages are JSON text frames with a `type`. The client speaks first and says which protocol
versions it understands; the server picks one:

```text
client: {"type":"hello","versions":[1]}
server: {"type":"welcome","version":1,"connectionId":"…"}
server: {"type":"event","id":"…","event":"note.created","occurredAt":"…","data":{…}}
client: {"type":"ping"}        server: {"type":"pong","serverTime":"…"}
```

| Close code | Why |
| --- | --- |
| 1008 (policy violation) | No `hello` within `Realtime:HandshakeTimeoutSeconds`, no common version, or the client fell too far behind |
| 1007 (invalid payload) | A frame that is not JSON |
| 1009 (message too big) | A frame over `Realtime:MaxMessageBytes` |

When the protocol changes in a way old clients cannot read, add version 2 to
`WebSocketSession.SupportedVersions` and keep 1 until the old clients are gone.

## Safety

- Browsers send cookies with a WebSocket from any site, so the WebSocket refuses a page from
  another origin (403) unless the origin is in `Cors:AllowedOrigins`. Clients that are not browsers
  send no `Origin` and are not affected.
- Each connection has a queue of `Realtime:MaxQueuedMessages` events. A client that cannot keep up
  is disconnected instead of growing the server's memory; it reconnects and reloads.

## Several instances

Each instance only reaches the clients connected to it. With more than one instance behind a load
balancer, add a backplane (for SignalR, the Redis backplane or Azure SignalR Service) and make
`RealtimePublisher` publish through it, or keep each user's connections on one instance with sticky
sessions. SignalR without WebSockets needs sticky sessions in any case.
