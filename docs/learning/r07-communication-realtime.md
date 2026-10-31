# R07, Communication and real-time (0.7.0): why the API talks to the world like this

Until R06 the API only answered requests. R07 makes it talk: it calls other services, pushes
changes to connected clients, sends notifications to people, and exchanges webhooks with other
systems. Four pieces, three of them options:

- **Outbound HTTP**, in every project: clients with timeouts, retries and a list of allowed hosts.
- `--realtime`: SignalR, server-sent events and raw WebSockets.
- `--notifications`: an in-app inbox, preferences, and email, push and SMS delivery.
- `--webhooks`: signed webhooks in and out.

Two ideas run through the whole release. **Anything that crosses the network fails sometimes**, so
every send has a timeout, a retry policy and a record of what happened. And **anything that comes
from outside is hostile until checked**: a URL can point at your own network, a webhook can be
forged or replayed, a browser page can open a WebSocket with your user's cookies.

## Contents

1. [One event, many listeners](#1-one-event-many-listeners)
2. [Outbound HTTP: IHttpClientFactory plus the resilience handler](#2-outbound-http-ihttpclientfactory-plus-the-resilience-handler)
3. [SSRF: a host list, or a check on the address](#3-ssrf-a-host-list-or-a-check-on-the-address)
4. [Three real-time channels, not one](#4-three-real-time-channels-not-one)
5. [Designing a WebSocket protocol](#5-designing-a-websocket-protocol)
6. [Cross-site WebSocket hijacking and tokens in the URL](#6-cross-site-websocket-hijacking-and-tokens-in-the-url)
7. [Back-pressure: what to do with a slow client](#7-back-pressure-what-to-do-with-a-slow-client)
8. [Standard Webhooks, not a home-made signature](#8-standard-webhooks-not-a-home-made-signature)
9. [Incoming webhooks: verify, store, answer, then work](#9-incoming-webhooks-verify-store-answer-then-work)
10. [Outgoing webhooks: deliveries are rows](#10-outgoing-webhooks-deliveries-are-rows)
11. [A small outbox in the database instead of a message broker](#11-a-small-outbox-in-the-database-instead-of-a-message-broker)
12. [Feature tables in their own DbContext](#12-feature-tables-in-their-own-dbcontext)
13. [The notification model: inbox, channels, preferences](#13-the-notification-model-inbox-channels-preferences)
14. [Push and SMS over plain HTTP, no vendor SDKs](#14-push-and-sms-over-plain-http-no-vendor-sdks)
15. [Catalog rules and the realtime preset](#15-catalog-rules-and-the-realtime-preset)
16. [Gotchas worth remembering](#16-gotchas-worth-remembering)

---

## 1. One event, many listeners

**The choice.** Endpoints publish what happened as an `AppEvent` (`note.created`, with the note's
response record as data) through `AppEvents`, after saving. `AppEvents` hands it to every
registered `IAppEventSink`. The real-time publisher is one sink; the webhook sink, which turns the
event into delivery rows, is another.

```csharp
// templates/ek-api/src/MyProject.Api/Common/AppEvents.cs (trimmed)
internal sealed record AppEvent(string Type, string? UserId, object Data);

internal interface IAppEventSink
{
    Task HandleAsync(AppEvent appEvent, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}
```

**Why.** Without it, `NoteEndpoints` would call the hub, then the webhook code, then whatever comes
next, and every new listener would mean editing every endpoint. With a sink list, the endpoint says
*what happened* once and the listeners decide what to do with it. It is the observer pattern, kept
in-process.

**Alternatives.** MediatR notifications do the same thing, but MediatR now needs a commercial
licence (the R01 rule). A message broker (RabbitMQ, Azure Service Bus) is the right tool when other
services listen too, but it is one more thing to run for a template whose listeners all live in the
same process.

**Trade-offs.** Publishing happens *after* `SaveChanges`, in a separate step. If the process dies
between the two, the note exists and the event is lost: no webhook goes out for it. The fix is a
transactional outbox (write the event row in the same transaction as the note), which needs the
event table in the same context as every entity that publishes. We left that out on purpose to keep
features independent (section 12), and the docs say real-time events are best effort. A failing sink
is logged and never fails the request: the user's note was saved, so the answer is still 201.

## 2. Outbound HTTP: IHttpClientFactory plus the resilience handler

**The choice.** Every call to another service goes through `AddOutboundClient`, which registers a
typed or named client with `IHttpClientFactory` and adds the standard resilience handler from
Microsoft.Extensions.Http.Resilience (MIT): a total timeout, a per-attempt timeout, retries with
exponential backoff and jitter, and a circuit breaker.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Http/OutboundHttp.cs (trimmed)
builder.AddStandardResilienceHandler().Configure((resilience, provider) =>
{
    var options = provider.GetRequiredService<IOptionsMonitor<OutboundClientOptions>>().Get(name);
    resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds);
    if (!options.RetryUnsafeMethods)
    {
        resilience.Retry.DisableForUnsafeHttpMethods();
    }
});
```

**Why `IHttpClientFactory`.** `new HttpClient()` per call exhausts sockets; one static client never
sees DNS changes. The factory pools handlers and recycles them, and gives each client a name to hang
configuration on.

**Why the resilience handler.** It is Polly v8 underneath, with defaults Microsoft tuned for HTTP:
which status codes are transient (5xx, 408, 429), honouring `Retry-After`, and a breaker that stops
calling a service that keeps failing. Writing those policies by hand is where people get the
details wrong (retrying a 400, retrying forever, no jitter so every client retries at the same
instant).

**Why not retry POST.** A POST that timed out may have succeeded on the other side. Retrying it can
charge a card twice. So unsafe methods are not retried unless the client says the call is safe to
repeat (`RetryUnsafeMethods`), typically because the other service honours an idempotency key (the
same idea as R03's `Idempotency-Key`, from the caller's side).

**Alternatives.** Raw Polly gives the same power with more code. Refit (MIT) generates clients from
interfaces and works on top of this; teams can add it per client. Flurl is pleasant but bypasses the
factory's handler pipeline.

**Trade-offs.** Two layers of retry can multiply: the resilience handler retries three times, and
the notification or webhook loops retry on their own schedule. That is why the loops' clients send
POSTs (not retried by the handler) and the webhook client has no resilience handler at all: one
place decides when to try again.

## 3. SSRF: a host list, or a check on the address

**The idea.** Server-side request forgery (SSRF) is making a server fetch a URL the attacker chose.
The server can reach things the attacker cannot: `localhost` admin ports, the database, the cloud
metadata endpoint at `169.254.169.254` that hands out credentials. Any feature that fetches a URL
from data (a webhook target, an avatar link, an import from a URL) is a candidate.

**The choice: two defences for two situations.**

- When the code knows which service it calls (FCM, Twilio), the client gets a **host allowlist**.
  `AllowedHostsHandler` runs before anything else and refuses any other host, so even a bug that
  builds the URL from user input cannot send the request elsewhere.
- When users choose the URL (webhook subscriptions), a list cannot help. The webhook client's
  `ConnectCallback` is `PublicNetwork.ConnectAsync`: it resolves the name itself and refuses
  loopback, private, link-local, carrier-grade NAT, multicast and other reserved ranges, then
  connects to an address it checked.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Http/PublicNetwork.cs (trimmed)
var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
var allowed = addresses.Where(IsPublic).ToArray();
if (allowed.Length == 0)
{
    throw new OutboundRequestRefusedException($"{host} has no public address; private and reserved networks are refused.");
}
```

**Why check at connect time, not only when the URL is saved.** Subscription URLs are checked when
saved too, but a name can resolve to a public address today and `10.0.0.5` tomorrow (DNS
rebinding). Checking the address the socket actually connects to closes that gap.

**Why redirects are off everywhere.** `HttpClient` follows redirects inside the primary handler,
after the allowlist handler has already said yes. A permitted host answering `302 Location:
http://169.254.169.254/` would walk straight past it.

**Trade-offs.** Development needs to deliver webhooks to `localhost`, so `AllowPrivateNetworks` and
`AllowHttp` exist and are on only in `appsettings.Development.json`. A setting that turns a
defence off is a risk; the docs say never to set them in production.

## 4. Three real-time channels, not one

**The choice.** The same events go out three ways: a SignalR hub, a server-sent events (SSE)
stream, and a raw WebSocket with our own protocol.

| | SignalR | SSE | Raw WebSocket |
| --- | --- | --- | --- |
| Direction | Both ways | Server to client | Both ways |
| Client | SignalR library | `EventSource`, `curl`, anything HTTP | Any WebSocket library |
| Reconnect, fallbacks | Built in | Built into `EventSource` | Yours to write |
| Works through strict proxies | Falls back to long polling | Yes, it is plain HTTP | Sometimes not |

**Why all three.** They serve different clients. SignalR is the easiest for apps that can use its
client (web, .NET, mobile). SSE is the simplest possible listener: it is just a long HTTP response,
.NET 10 has `TypedResults.ServerSentEvents`, and a dashboard or a script needs nothing else. Raw
WebSockets are for clients SignalR does not serve well, such as a device or a game in another
language. Building them on one publisher (`RealtimePublisher` with one queue per connection) means
the three cost little more than one.

**Alternatives.** SignalR alone is the common choice and would cover most apps. Socket.IO is the
Node.js world's equivalent and has no maintained .NET server. gRPC streaming needs HTTP/2 end to end
and does not run in browsers without a proxy.

**Trade-offs.** Three channels are three things to document and test, and the raw WebSocket's
protocol is ours to keep compatible (section 5). Each instance only knows its own connections; with
more than one instance you need a backplane (Redis, Azure SignalR) or sticky sessions. The template
does not add one, because most projects start with one instance, and `docs/realtime.md` says where
it goes.

## 5. Designing a WebSocket protocol

A WebSocket gives you messages, nothing else. Everything a protocol needs is yours to define:
framing, versioning, keep-alive and error reporting.

**Framing.** Every message is a JSON text frame with a `type` field (`hello`, `welcome`, `event`,
`ping`, `pong`, `error`). One field to switch on keeps clients simple, and JSON is readable in a
browser's network tab.

**A version handshake.** The client speaks first and lists the versions it understands; the server
picks the highest it also supports, or closes the connection:

```text
client: {"type":"hello","versions":[1]}
server: {"type":"welcome","version":1,"connectionId":"…"}
```

This is how a protocol changes without breaking old clients: add version 2, serve both, drop 1 when
nobody sends it. Without a handshake, the first incompatible change breaks every installed app at
once, and mobile apps stay installed for years.

**Close codes say why.** WebSocket close codes are part of the standard (RFC 6455): 1008 (policy
violation) for a missing or unsupported `hello`, 1007 (invalid payload) for a frame that is not
JSON, 1009 (message too big) for a frame over the limit. A client can tell "upgrade the app" from
"bug" from "reconnect".

**Timeouts.** A connection that never says `hello` is closed after `HandshakeTimeoutSeconds`, so
idle sockets cannot pile up. Keep-alive pings at the WebSocket level (`KeepAliveInterval`) keep
proxies from closing quiet connections; the app-level `ping`/`pong` lets a client measure the round
trip.

## 6. Cross-site WebSocket hijacking and tokens in the URL

**The attack.** Browsers apply the same-origin policy to `fetch`, but not to opening a WebSocket. A
page on `evil.example` can open `wss://your-api/v1/realtime/ws`, and the browser attaches the user's
cookies. If the API accepts cookie sessions (R02's cookie mode), the attacker's page now reads the
user's events. This is cross-site WebSocket hijacking (CSWSH), CSRF's cousin.

**The choice.** The WebSocket endpoint checks the `Origin` header. It accepts no `Origin` (not a
browser), its own origin, and origins in `Cors:AllowedOrigins`; anything else gets 403 before the
upgrade.

**Tokens in the query string.** Browsers cannot set an `Authorization` header on a WebSocket or an
`EventSource`. The standard workaround, and the one SignalR's clients use, is `?access_token=…`. The
template accepts it **only on `/v1/realtime` paths**, because URLs end up in logs and browser
history. Keep tokens short-lived. API keys cannot connect at all: a long-lived key in a URL is worse.

**Alternatives.** A one-time ticket (get a short ticket over normal HTTP, pass it in the URL) avoids
putting the access token in the URL at all. It is more code and one more round trip; a good next
step if tokens live long.

## 7. Back-pressure: what to do with a slow client

**The problem.** The server produces events faster than one client reads them (a phone on a bad
network). If the server buffers without limit, one slow client grows memory until the process dies.

**The choice.** Each connection gets a bounded `Channel<T>` of `MaxQueuedMessages` (256). When it is
full, the connection is completed with `SlowClientException` and closed (1008, "too far behind;
reconnect"). The client reconnects and reloads current state.

```csharp
// templates/ek-api/src/MyProject.Api/Features/Realtime/RealtimePublisher.cs (trimmed)
_queue = Channel.CreateBounded<RealtimeMessage>(new BoundedChannelOptions(capacity) { SingleReader = true });
…
if (!_queue.Writer.TryWrite(message))
{
    _queue.Writer.TryComplete(new SlowClientException());
}
```

**Alternatives.** Drop the oldest events silently (`BoundedChannelFullMode.DropOldest`): the client
stays connected but has a gap it does not know about. Blocking the publisher: one slow client slows
every request that publishes. Disconnecting is the honest option, and since events are best effort
(section 1), clients must handle reconnects anyway.

## 8. Standard Webhooks, not a home-made signature

**The idea.** A webhook is an HTTP POST from one system to another. The receiver must know it is
genuine (not forged), unchanged, and not an old message sent again (a replay). The usual answer is
an HMAC: both sides share a secret, the sender sends `HMAC-SHA256(secret, message)`, the receiver
computes it again and compares.

**The choice.** The [Standard Webhooks](https://www.standardwebhooks.com/) specification:

- headers `webhook-id`, `webhook-timestamp` and `webhook-signature`;
- the signed content is `{id}.{timestamp}.{body}`, so the id and time cannot be changed either;
- signatures look like `v1,<Base64>`, and several can be sent at once, space-separated, for secret
  rotation;
- secrets look like `whsec_<Base64>`.

```csharp
// templates/ek-api/src/MyProject.Api/Features/Webhooks/StandardWebhooks.cs (trimmed)
if (received.Any(signature => CryptographicOperations.FixedTimeEquals(signature, expected)))
```

**Why a standard.** Every provider used to invent its own header names and signed content, and
every receiver wrote its own check, often wrongly. Standard Webhooks is what Svix, Twilio Segment,
Clerk and others converged on, and it has verification libraries in most languages, so the
template's subscribers can verify with a library instead of copying our code. The unit tests check
our implementation against the specification's published test vector.

**Why the details matter.** Comparing with `==` leaks, through timing, how many leading bytes
matched; `FixedTimeEquals` takes the same time whatever the input. Signing the raw body bytes means
the receiver must verify **before** parsing JSON: a parser that reorders keys produces different
bytes. The timestamp, with a tolerance of five minutes, stops replays: a captured message cannot be
sent again tomorrow.

**Alternatives.** Mutual TLS proves the sender at the connection level, but needs certificates on
both sides. An API key in a header proves the sender but not that the body is unchanged, and leaks
with every log of the request. Asymmetric signatures (Ed25519, also allowed by the standard as
`v1a`) let receivers verify without holding a secret that could sign; worth adding when subscribers
are many and untrusted.

## 9. Incoming webhooks: verify, store, answer, then work

**The choice.** `POST /v1/webhooks/incoming/{source}`:

1. verifies the signature and timestamp against the source's secrets (401 if wrong);
2. answers 200 without storing if this `webhook-id` was already received;
3. stores the message and answers **202 at once**;
4. a background loop hands it to the source's `IIncomingWebhookHandler`, with retries.

**Why not handle it in the request.** Senders have short timeouts (often 5 to 30 seconds) and retry
when they do not get a 2xx in time. Slow handling in the request causes duplicate deliveries,
which cause more load. Answering fast and working later breaks that loop.

**Why duplicate detection.** Every sender delivers *at least once*: a lost 2xx means the same
message arrives again. The unique index on `(Source, MessageId)` makes "store" happen once even when
two copies arrive together (the second insert fails, and we answer 200). This is R04's at-least-once
lesson again: handlers must still be idempotent, because a handler can crash after its effect and
before the row is marked done.

**Trade-offs.** The payload is stored before it is processed, so it sits in the database (until
`RetentionDays`). If senders put personal data in webhooks, that is personal data you now keep;
shorten the retention or store only what the handler needs.

## 10. Outgoing webhooks: deliveries are rows

**The choice.** For each event, each enabled subscription that wants its type gets a
`WebhookDelivery` row. `WebhookDispatchLoop` sends due rows, one attempt per run, and schedules the
next attempt from `RetryDelaysSeconds` (5 s, 5 min, 30 min, 2 h, 5 h, 10 h). The row is also the
**delivery log** subscribers and admins read, with each attempt's status code and error.

**Why a schedule this long.** A subscriber's endpoint can be down for hours (a deploy, an outage).
Retrying for most of a day with growing gaps gets through those without hammering a struggling
server. This is close to what Stripe and Svix do.

**Why 410 Gone switches a subscription off.** It is the subscriber saying "this endpoint is gone
for good". Retrying it for a day helps nobody. Other 4xx answers are retried, because they are
often temporary misconfigurations on the subscriber's side.

**Why the secret is shown once.** Like an API key (R02), the secret is returned on creation and on
rotation only. It must be stored in a form we can sign with, so unlike an API key it cannot be
hashed; showing it once at least keeps it out of every later response and log.

## 11. A small outbox in the database instead of a message broker

**The choice.** Webhook deliveries, incoming webhooks and notification deliveries all implement
`IOutboxItem` (status, attempts, next attempt, lock). One helper, `Outbox.ClaimDueAsync`, lets a
loop take due rows, and the claim is a conditional update, so two API instances never send the same
row at once:

```csharp
// templates/ek-api/src/MyProject.Api/Data/ModuleDatabase.cs (trimmed)
var updated = await items
    .Where(item => item.Id == id && item.Status == OutboxStatus.Pending && (item.LockedUntil == null || item.LockedUntil < now))
    .ExecuteUpdateAsync(update => update.SetProperty(item => item.LockedUntil, until), cancellationToken);
if (updated == 1)
{
    claimed.Add(id);
}
```

**Why not Hangfire or the job queue from R04.** They would work for `--jobs hangfire`, but webhooks
and notifications must work with `--jobs none` too, and the in-process queue loses work on restart.
The rows also *are* the delivery log, which a job store is not.

**Why not a broker (RabbitMQ, Kafka).** One more service to run, back up and monitor, for a volume
a table handles easily. The pattern moves to a broker cleanly when it needs to.

**The lease.** `LockedUntil` is a lease, not a lock: if an instance crashes mid-send, the lease runs
out and another instance picks the row up. That is why sends are at least once, and why receivers
deduplicate by `webhook-id`.

## 12. Feature tables in their own DbContext

**The problem.** R03 stores migrations per database provider and per option set
(`.migrations/<provider>/<auth>`), because the model differs per combination. Adding two more
database options to that would multiply the sets: providers × auth modes × storage × notifications
× webhooks.

**The choice.** Notifications and webhooks have their own contexts (`NotificationsDbContext`,
`WebhooksDbContext`) in the same database, each with its own migrations folder and its **own
migration history table** (`__EFMigrationsHistory_Webhooks`). Startup migrates `AppDbContext`, then
every registered module context. Turning a feature on adds one migration set per provider: six in
all, instead of dozens.

**Why it works.** The module tables never reference the app's tables. User ids are plain columns,
not foreign keys to `AspNetUsers`, which also makes notifications work with OIDC, where users live
at the provider.

**Trade-offs.** No foreign key means no cascade: deleting a user does not delete their
notifications. `docs/notifications.md` says which tables to clean. The two contexts cannot share a
transaction with `AppDbContext` without extra work, which is the reason section 1's event publishing
is not transactional. And each module context must keep its entity configuration inside
`OnModelCreating`: `AppDbContext` applies every `IEntityTypeConfiguration` in the assembly, so a
separate configuration class would put webhook tables into the app's migrations too.

## 13. The notification model: inbox, channels, preferences

**The choice.**

- A `Notification` row per user is the in-app inbox entry, with `ReadAt`. The inbox is always on.
- Each notification **type** (`announcement`) has default channels in code (`NotificationTypes`).
- A `NotificationPreference` row exists only when a user changed a default: (user, type, channel)
  → on/off.
- Sending writes the notification and one delivery row per channel (per device for push) in one
  `SaveChanges`; the dispatcher sends them later.

**Why per type, not one global switch.** Users want "security alerts by SMS, marketing nowhere".
Per-type preferences are what app stores and privacy laws increasingly expect. Storing only
overrides keeps the table small and lets you change a default for everyone who never touched it.

**Why SMS defaults to off.** Each text costs money and interrupts. Users opt in.

**Why links are checked.** A notification link ends up in an email and on a lock screen. Only app
paths and `https` URLs are accepted, so an admin form cannot send `javascript:` or a lookalike
`//evil.example` link.

## 14. Push and SMS over plain HTTP, no vendor SDKs

**The choice.** Firebase Cloud Messaging, Apple Push Notification service and Twilio are called
with `HttpClient`, through allowlisted outbound clients (section 2). Each sender is about forty
lines:

- **FCM HTTP v1** needs an OAuth access token. We sign a JWT with the service account's RSA key
  (RS256), exchange it at `oauth2.googleapis.com`, and reuse the token for its hour.
- **APNs** takes a provider token: a JWT signed with the `.p8` key (ES256). Apple asks for it to be
  reused for up to an hour, not made per request. Requests must use HTTP/2.
- **Twilio** is a form POST with Basic auth.

**Why no SDKs.** FirebaseAdmin (Apache-2.0) pulls in the Google API client stack for one POST.
APNs libraries for .NET are small, rarely maintained projects. Twilio's SDK is fine but wraps one
endpoint we use. Plain HTTP means: no extra dependencies to audit and update, the same timeout and
allowlist rules as everything else, and stub handlers in unit tests that check exactly what is
sent. The unit tests verify the JWT signatures with the public keys.

**How failures are sorted.** Each provider says when a device token is dead (FCM `UNREGISTERED`,
APNs 410 or `BadDeviceToken`); the device is removed instead of retried forever. Answers that
retrying cannot fix (bad credentials, an invalid number, Twilio's 400s) throw
`NotificationRejectedException` and fail the delivery at once. Everything else is retried on the
schedule.

**Alternatives.** A notification service (OneSignal, Azure Notification Hubs, Novu) handles
devices, templates and preferences for you, but is another vendor holding your users' data and
often paid at scale. The router (`PushRouter`, `SmsRouter`) is where one would plug in.

**Trade-offs.** We own the protocol details. If Apple or Google change their API, the template
changes, not a package version. Both APIs have been stable for years, which made this acceptable.

## 15. Catalog rules and the realtime preset

- `notifications-need-accounts`: notifications belong to a signed-in user, so `--notifications`
  with `--auth none` gives local accounts (`identity`). OIDC and external sign-in work too.
- `webhooks-need-database`: subscriptions and logs are rows, so `--db none` becomes PostgreSQL, as
  for `--storage s3` in R06.
- `--realtime` has no rule: it works with no database and no sign-in (everyone hears everything).
- The `realtime` preset is `identity + postgres + hosted jobs + realtime + notifications`: the
  shape of an app where signed-in users see things change live. `saas-api` gained notifications and
  webhooks, which most SaaS products end up needing.

Outbound HTTP is not an option. Every API calls something sooner or later, and a safe default is
worth the one package.

## 16. Gotchas worth remembering

- **SQL Server index keys are limited to 1,700 bytes**, and `nvarchar` columns to 4,000 characters
  unless they are `max`, which cannot be indexed. A 4,096-character device token with a unique
  index failed migration on SQL Server only. Tokens are now 512 characters. Run every provider's
  tests before trusting a column size.
- **Configuration binding appends to arrays.** An options class with
  `RetryDelaysSeconds = [5, 300]` plus `[1, 1]` from settings gives four entries, not two. Arrays
  default to empty in code; the real defaults live in `appsettings.json`.
- **`dotnet format` sorts `using` lines across `#if` blocks.** A conditional `using` placed at the
  end of the list passes in one combination and fails the import-order check in another. Put it
  where it sorts.
- **A parameter used only inside `#if` is unused in the other build** (IDE0060, an error with
  `-warnaserror`). Name it `_` in that build.
- **Leftover checks catch words, not just code.** With `--auth none`, the word "Bearer" in a test
  helper failed acceptance. Wrap auth-only lines in `#if (UseAuth)`, docs included.
- **A template source folder must exist.** The migration sources for a new module are mapped in
  `template.json`; while the folder was missing, the template failed to load (exit code 103).

## Try it

```bash
# Real-time without accounts: open the event stream, then create a note in another terminal.
ek new Live --realtime -y && cd Live && dotnet run --project src/Live.Api
curl -N http://localhost:5080/v1/realtime/events
curl -X POST http://localhost:5080/v1/notes -H 'Content-Type: application/json' -d '{"title":"Hi","content":"there"}'

# The realtime preset: accounts, live events and notifications.
ek new Inbox --preset realtime -y

# Webhooks: subscribe a local receiver (development allows http and localhost), then watch the log.
ek new Hooks --webhooks -y
```

- Read `StandardWebhooksTests` and change one byte of the body: the signature check fails.
- Set `Realtime:MaxQueuedMessages` to 1 and create notes quickly while a slow client reads: it is
  disconnected with 1008.
- Point a webhook subscription at `https://169.254.169.254/` and see the refusal.

## Further reading

- [Standard Webhooks specification](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md)
- [OWASP: Server-Side Request Forgery Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html)
- [Build resilient HTTP apps (Microsoft.Extensions.Http.Resilience)](https://learn.microsoft.com/dotnet/core/resilience/http-resilience)
- [RFC 6455, The WebSocket Protocol](https://www.rfc-editor.org/rfc/rfc6455) (close codes in section 7.4)
- [Cross-Site WebSocket Hijacking](https://christian-schneider.net/CrossSiteWebSocketHijacking.html)
- [Server-sent events (MDN)](https://developer.mozilla.org/docs/Web/API/Server-sent_events)
- [FCM HTTP v1 API](https://firebase.google.com/docs/cloud-messaging/send/v1-api) and
  [APNs token-based connection](https://developer.apple.com/documentation/usernotifications/establishing-a-token-based-connection-to-apns)
- [Transactional outbox pattern](https://microservices.io/patterns/data/transactional-outbox.html)
