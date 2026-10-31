# Calling other services

Every HTTP call to another service goes through a client registered with `AddOutboundClient`. It
gives the client a timeout, retries, a circuit breaker and a list of hosts it may call.

```csharp
services.AddOutboundClient<IWeatherClient, WeatherClient>("weather", "api.weather.example");

internal sealed class WeatherClient(HttpClient http) : IWeatherClient
{
    public Task<Forecast?> GetAsync(string city, CancellationToken cancellationToken) =>
        http.GetFromJsonAsync<Forecast>($"https://api.weather.example/forecast?city={Uri.EscapeDataString(city)}", cancellationToken);
}
```

Settings come from `OutboundHttp:<name>`, all optional:

| Setting | Default | Meaning |
| --- | --- | --- |
| `BaseAddress` | none | Lets the code use relative URLs |
| `AllowedHosts` | `[]` | Hosts allowed besides the ones in code; `*.example.com` allows subdomains |
| `TimeoutSeconds` | 30 | The whole call, retries included |
| `AttemptTimeoutSeconds` | 10 | One attempt; must be less than `TimeoutSeconds` |
| `MaxRetries` | 3 | Retries of failed attempts (5xx, 408, 429, timeouts), with backoff; 0 turns retries off |
| `RetryUnsafeMethods` | false | Retry POST, PUT, PATCH and DELETE too; only for calls that are safe to repeat |
| `AllowHttp` | false | Allow plain `http://`; for local development only |

## Why each piece is there

- **Timeouts.** `HttpClient`'s default is 100 seconds; a slow service would hold requests and
  threads that long. The resilience handler owns every timeout, so `HttpClient.Timeout` is off.
- **Retries only for safe methods.** A POST that timed out may have worked; sending it again could
  charge a card twice. Retry those only with an idempotency key the other service honours.
- **Circuit breaker.** When most calls fail, it stops calling for a while, so a broken service
  fails fast instead of slowing everything down.
- **Allowed hosts.** A URL that comes from data (a callback, an image link) could point the server
  at an internal address, such as a cloud metadata endpoint: server-side request forgery (SSRF).
  The client refuses any host that is not on its list with `OutboundRequestRefusedException`, before
  connecting. Redirects are not followed, because a redirect could lead past the list.
- **Cancellation.** Pass the request's `CancellationToken` on, so a call stops when the caller
  goes away.

## URLs chosen by users

When users choose the URL (a webhook endpoint, for example), a host list cannot help. Use a client
whose primary handler connects through `PublicNetwork.ConnectAsync`: it resolves the name, then
refuses loopback, private, link-local and other reserved addresses before connecting, which also
covers a public name that resolves to a private address.

```csharp
services.AddHttpClient("user-urls").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectCallback = PublicNetwork.ConnectAsync,
});
```

## Testing

Replace the primary handler in tests, so no test calls the real service:

```csharp
services.AddHttpClient("weather").ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
```
