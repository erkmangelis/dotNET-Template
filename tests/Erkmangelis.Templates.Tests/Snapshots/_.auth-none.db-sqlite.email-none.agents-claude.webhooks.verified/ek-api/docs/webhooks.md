# Webhooks

Webhooks in both directions, signed the [Standard Webhooks](https://www.standardwebhooks.com/) way:
other services call this API (incoming), and this API calls its subscribers (outgoing). Both keep a
log in the database and retry on their own.

Every message carries three headers:

| Header | Value |
| --- | --- |
| `webhook-id` | The message id. It stays the same on every retry, so the receiver can drop repeats |
| `webhook-timestamp` | Unix seconds when it was sent |
| `webhook-signature` | `v1,<Base64 HMAC-SHA256 of "{id}.{timestamp}.{body}">`, keyed with the secret's bytes |

Secrets look like `whsec_<Base64>`. The libraries at standardwebhooks.com check these signatures in
most languages, so subscribers do not have to write the check themselves.

## Incoming

Senders POST to `/v1/webhooks/incoming/{source}`. For each source, configure its secrets and
register a handler:

```json
"Webhooks": { "Incoming": { "Sources": { "billing": { "Secrets": [ "whsec_…" ] } } } }
```

```csharp
services.AddIncomingWebhookHandler<BillingWebhookHandler>("billing");

internal sealed class BillingWebhookHandler(AppDbContext db) : IIncomingWebhookHandler
{
    public async Task HandleAsync(IncomingWebhookMessage message, CancellationToken cancellationToken)
    {
        // message.EventType, message.Payload (JSON) …
    }
}
```

What happens to a request:

1. An unknown source answers 404.
2. The signature is checked against each of the source's secrets (more than one while you rotate).
   A missing header, a wrong signature, or a timestamp more than `ToleranceSeconds` (300) from
   the server's clock answers 401. The timestamp check stops someone replaying an old message.
3. A `webhook-id` already received answers 200 and is not stored again: senders retry until they
   get a 2xx, so repeats are normal.
4. The message is stored and the request answers 202 at once. The handler runs in the background
   (`IncomingWebhookLoop`), so a slow handler never makes the sender time out.

A handler that throws is retried after each of `Incoming:RetryDelaysSeconds`, then the message is
marked failed. A source with no handler fails at once. `GET /v1/webhooks/incoming` lists received
messages and `POST /v1/webhooks/incoming/{id}/retry` hands a failed one to its handler again.
Handlers must be safe to run twice for the same message.

Signatures are checked against the exact bytes received. Do not put middleware in front of this
route that reads and rewrites the body.

## Outgoing

Subscribers register a URL and the event types they want:

```http
POST /v1/webhooks/subscriptions
{ "url": "https://hooks.example.com/notes", "eventTypes": ["note.created", "note.deleted"] }
```

The answer holds the signing secret; it is shown only then and on `POST …/{id}/secret`, which
replaces it. When an endpoint publishes an `AppEvent`, every enabled subscription to its type gets
a delivery row, and `WebhookDispatchLoop` POSTs it:

```json
{ "type": "note.created", "timestamp": "2026-10-31T09:00:00Z", "data": { … } }
```

- A 2xx answer is success. Anything else, a timeout (`Outgoing:TimeoutSeconds`) or a network
  error is retried after each of `Outgoing:RetryDelaysSeconds` (5 s, 5 min, 30 min, 2 h, 5 h,
  10 h), with the same `webhook-id`. Then the delivery is marked failed.
- 410 Gone turns the subscription off; `PUT` it with `"enabled": true` to turn it on again.
- `GET …/{id}/deliveries` is the delivery log, with each attempt's status code and error, and
  `POST …/{id}/deliveries/{deliveryId}/retry` sends a failed one again.
- `POST …/{id}/test` queues a `webhook.test` delivery, to check an endpoint.

Deliveries are at least once and may arrive out of order. Tell subscribers to drop a `webhook-id`
they have seen and to answer 2xx quickly, doing slow work afterwards.

### Safety

Subscribers choose the URL, so the API could be pointed at its own network (server-side request
forgery). Subscription URLs must be `https` and may not name a private or reserved address, and
the delivery client checks the address again after DNS (`PublicNetwork`), never follows redirects
and sends no cookies or credentials. Development allows `http` and local addresses
(`Outgoing:AllowHttp`, `Outgoing:AllowPrivateNetworks`), so you can point a subscription at a local
tool; never turn these on in production.

## Tables

Webhook tables have their own `WebhooksDbContext`, migrations folder (`Data/Migrations/Webhooks`)
and migration history table, and are migrated with the app's database. Delivery and incoming logs
older than `RetentionDays` (30) are removed by the loops.
