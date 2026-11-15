# R08, Operations and performance (0.8.0): why the API is watched, cached and checked like this

Through R07 the template answered the question "does it work?". R08 answers the questions that come
after go-live: *Is it up? Is it fast? Where does the time go? Is it still safe?* Six pieces:

- `--cache memory|redis`: HybridCache for values, output caching for whole responses.
- `--monitoring`: Prometheus, Grafana, Tempo, Loki and alerts in a compose profile.
- **k6 load testing**, in every project and in CI, with a measured number.
- **Security scanning** in CI: vulnerable packages, secrets in the history, CodeQL.
- **Localization**: problem responses in English or Turkish.
- **Feature flags**: switches in configuration, with Microsoft.FeatureManagement.

One idea runs through the release: **measure first, then change**. Caching without a load test is
guessing, a load test without metrics tells you *that* it is slow but not *why*, and an alert you
never tested is a promise you cannot keep. So the pieces come together: k6 makes load, Prometheus
and Tempo show where the time went, the cache fixes what they show, and k6 proves it.

## Contents

1. [HybridCache, not IMemoryCache or IDistributedCache](#1-hybridcache-not-imemorycache-or-idistributedcache)
2. [What to cache, and the four rules that keep it correct](#2-what-to-cache-and-the-four-rules-that-keep-it-correct)
3. [Redis as the second level, and why Valkey runs it](#3-redis-as-the-second-level-and-why-valkey-runs-it)
4. [Output caching only for same-for-everyone responses](#4-output-caching-only-for-same-for-everyone-responses)
5. [Metrics: Prometheus pulls, OTLP pushes the rest](#5-metrics-prometheus-pulls-otlp-pushes-the-rest)
6. [The monitoring stack: a collector, then one store per signal](#6-the-monitoring-stack-a-collector-then-one-store-per-signal)
7. [Four alerts, on symptoms, with tests](#7-four-alerts-on-symptoms-with-tests)
8. [k6 for load testing, with thresholds that fail CI](#8-k6-for-load-testing-with-thresholds-that-fail-ci)
9. [Three security scans, each catching what the others miss](#9-three-security-scans-each-catching-what-the-others-miss)
10. [Localization: translate at the edge, keep code in English](#10-localization-translate-at-the-edge-keep-code-in-english)
11. [Only the UI culture changes: the Turkish i](#11-only-the-ui-culture-changes-the-turkish-i)
12. [Feature flags with Microsoft.FeatureManagement](#12-feature-flags-with-microsoftfeaturemanagement)
13. [Catalog: a three-way cache choice, and the presets](#13-catalog-a-three-way-cache-choice-and-the-presets)
14. [Gotchas worth remembering](#14-gotchas-worth-remembering)

---

## 1. HybridCache, not IMemoryCache or IDistributedCache

**The choice.** Values are cached with `HybridCache` (Microsoft.Extensions.Caching.Hybrid, MIT,
GA since .NET 9). With `--cache memory` it uses the process's memory; with `--cache redis`, memory
first and Redis second.

```csharp
// templates/ek-api/src/MyProject.Api/Features/Notes/NoteCache.cs (trimmed)
public ValueTask<Note?> FindAsync(Guid id, INoteStore store, CancellationToken cancellationToken) =>
    cache.GetOrCreateAsync(
        Key(id),
        (store, id),
        static async (state, token) => await state.store.FindAsync(state.id, token),
        tags: _tags,
        cancellationToken: cancellationToken);
```

**Why.** .NET had two cache interfaces before, and each one left work to you:

- `IMemoryCache` is fast but per process, and `GetOrCreateAsync` has no **stampede protection**: when
  a popular entry expires, every request that arrives before the first one finishes goes to the
  database. Under load that is the moment the database falls over.
- `IDistributedCache` (Redis) is shared but works on `byte[]`: you serialize yourself, every read is a
  network trip, and it has no stampede protection either.

`HybridCache` is both behind one call: it checks memory, then the second level, then calls your
factory **once** for all concurrent callers of the same key, and stores the result in both. It also
serializes values, so each caller gets its own copy and cannot corrupt the cached one, and it has
tags for removing a group of entries.

The `(store, id)` state argument and the `static` lambda are a small performance detail: the lambda
captures nothing, so no closure is allocated on every call, even on a hit.

**Alternatives.**
- *FusionCache* (MIT): older and richer (fail-safe, a backplane that tells other instances to drop
  their memory copy, soft timeouts). A fine choice; it can even be plugged in *behind* the
  `HybridCache` API. We kept the first-party package so generated projects start with less to learn,
  and because code written against `HybridCache` can switch to FusionCache later without changing.
- *EasyCaching, CacheManager*: less active.
- *Hand-rolled `IMemoryCache` plus a `SemaphoreSlim` per key*: the classic stampede fix, and exactly
  the code `HybridCache` exists so you do not write.

**Trade-offs.** Without a backplane, an instance does not hear when another instance changes a note,
so its memory copy can be up to `LocalExpirationSeconds` (30 s) old. Section 2 is about living with
that. Serializing every value also costs some CPU on every hit; types that are truly immutable can
opt out with `[ImmutableObject(true)]`.

## 2. What to cache, and the four rules that keep it correct

**The choice.** One read path is cached, as the example: `GET /v1/notes/{id}`. Writes never read
from the cache, every save removes the entry, and ownership is checked on the cached note.

```csharp
// templates/ek-api/src/MyProject.Api/Features/Notes/NoteEndpoints.cs (trimmed)
var note = await noteCache.FindAsync(id, store, cancellationToken);
if (await OwnedOrNullAsync(note, user, authorization) is not { } owned) { return TypedResults.NotFound(); }
...
// In UpdateAsync, after SaveChangesAsync succeeds:
await noteCache.ForgetAsync(id, cancellationToken);
```

**Why.** "There are only two hard things in computer science: cache invalidation and naming things."
The four rules are what makes the hard one manageable:

1. **Cache only where a slightly old value is fine.** Showing a note is fine. Checking an `If-Match`
   version before an update (R03) is not: against a stale copy it would let a lost update through.
   So `UpdateAsync` and `DeleteAsync` read the store, never the cache.
2. **Remove after every save.** *Remove*, not *update*: writing the new value into the cache races
   with a concurrent read that might put the old value back. Removing means the next read loads the
   truth. The removal happens *after* the save, so a failed save leaves the cache alone.
3. **Check access after the cache.** The key is `notes:{id}`, with no user in it, so the cached note
   is shared. The owner check runs on it exactly as on a database row, so another user still gets 404.
   Putting the user id in the key would also be safe, but would store one copy per user for no gain.
4. **"Not found" is cached too.** `GetOrCreateAsync` caches `null`. Creating a note removes its key
   for that reason, even though a brand-new id cannot have been asked for before (unless a client
   guessed it, which is exactly when you want this right).

**Alternatives.** *Cache the list endpoint*: lists depend on filters, paging and the user, so the key
space explodes and any write would have to invalidate all of it (tags make this possible, but the hit
rate is low). *Cache in EF Core* (second-level cache libraries): invisible, so the rules above are
broken without anyone noticing.

**Trade-offs.** The example only pays off for data that is read far more often than written. The
integration tests prove the rules (a changed store is not seen while cached, an update is seen at once,
another user gets 404), not that caching makes notes faster; for a small table it often does not.

## 3. Redis as the second level, and why Valkey runs it

**The choice.** `--cache redis` registers Redis (StackExchange.Redis through
`AddStackExchangeRedisCache`) as `HybridCache`'s second level, with **one** `IConnectionMultiplexer`
for the process. Compose, Aspire and the tests run **Valkey 9** as the server.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Caching/CachingSetup.cs (trimmed)
services.AddSingleton<IConnectionMultiplexer>(provider =>
{
    var configuration = ConfigurationOptions.Parse(...ConnectionString!);
    configuration.AbortOnConnectFail = false;
    configuration.ClientName = builder.Environment.ApplicationName;
    return ConnectionMultiplexer.Connect(configuration);
});
services.AddStackExchangeRedisCache(_ => { });
services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((redis, provider) =>
{
    redis.ConnectionMultiplexerFactory = () => Task.FromResult(provider.GetRequiredService<IConnectionMultiplexer>());
    redis.InstanceName = $"{builder.Environment.ApplicationName}:";
});
```

**Why.**
- **One multiplexer.** A `ConnectionMultiplexer` is designed to be shared: it pipelines every
  command over a couple of sockets. Creating one per request is the most common StackExchange.Redis
  mistake and exhausts connections. Sharing ours with the cache and the health check means one
  connection, one place to configure it.
- **`AbortOnConnectFail = false`.** Otherwise a Redis that is down at start-up throws and the API does
  not start. With it, the multiplexer keeps retrying in the background, and `HybridCache` falls back to
  the database. A cache must never be a reason the API is down.
- **`InstanceName`** prefixes every key with the app's name, so two apps can share one Redis without
  reading each other's `notes:…`.
- **Valkey, not Redis.** In 2024 Redis moved from BSD to a source-available licence (RSAL/SSPL), and
  later added AGPL as a third option. The Linux Foundation forked the last BSD version as Valkey,
  which AWS, Google and most distributions now ship. It speaks the same protocol, so the client and
  the code say "Redis" while the container is Valkey. This keeps the template's standing rule: no
  default that needs a commercial licence, and nothing you have to ask a lawyer about.
- **Compose settings.** No persistence (`--save "" --appendonly no`) and `allkeys-lru` eviction at
  `CACHE_MAX_MEMORY`: it is a cache, so losing it costs only slower reads, and a full cache should
  drop old keys, not refuse writes. A password is required because every container on the network
  could otherwise read and write it.

**Alternatives.** *Garnet* (Microsoft, MIT, Redis protocol): fast, but younger and less known on
small machines. *Memcached*: no tags, no data types, and `HybridCache` support only through third
parties. *SQL Server or PostgreSQL as `IDistributedCache`*: works, but puts cache load on the database
you were trying to protect.

**Trade-offs.** `/health/ready` fails while Redis is down, which takes the instance out of a load
balancer even though it could keep serving from the database. That is the safer default for most
setups (it surfaces the problem), and the doc says how to drop the tag if you prefer to stay in.

## 4. Output caching only for same-for-everyone responses

**The choice.** `AddOutputCache` with one named policy, `shared`, used on the OpenAPI document
(`.CacheOutput(CachePolicies.Shared)`). Output caching keeps the whole response in each instance's
memory.

**Why.** Output caching skips the endpoint entirely, which is the biggest possible saving, but it
keys on the URL, not on the user. ASP.NET Core's default policy therefore refuses to cache requests
with an `Authorization` header or a cookie, and responses that set a cookie. Those rules make it
safe for public data and useless for per-user data, which is exactly the split we want:
per-user data goes through `HybridCache` behind an access check (section 2).

The OpenAPI document is a good example because generating it reflects over every endpoint, it is
the same for everyone, and it changes only on deploy.

**Alternatives.** *Response caching middleware* (`AddResponseCaching`): it follows HTTP cache headers
from the client, so a client sending `Cache-Control: no-cache` bypasses it, which makes it a load-test
loophole. Output caching is the server-controlled successor. *A CDN* in front: the right answer for
truly public, heavy traffic, and complementary.

**Trade-offs.** Memory per instance, not shared: with three instances the document is built three
times. Output caching can use Redis too (`AddStackExchangeRedisOutputCache`); we did not wire that, to
keep one Redis use to understand.

## 5. Metrics: Prometheus pulls, OTLP pushes the rest

**The choice.** With `--monitoring`, the API serves `/metrics` for Prometheus to scrape, on a
**separate port** (9464). Traces and logs still go out over OTLP. Without the setting, everything goes
over OTLP, as in R01.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Observability.cs (trimmed)
if (prometheus)
{
    telemetry.WithTracing(tracing => tracing.AddOtlpExporter()).WithLogging(logging => logging.AddOtlpExporter());
}
else
{
    telemetry.UseOtlpExporter();
}
...
app.MapPrometheusScrapingEndpoint(PrometheusPath)
    .RequireHost($"*:{port}")
    .DisableRateLimiting()
    .ExcludeFromDescription();
```

**Why.**
- **Pull for metrics.** Prometheus scraping gives you `up`, a free health signal: if a scrape fails,
  Prometheus knows the target is down, which a push system can only infer from silence. It is also
  the format every Grafana dashboard and alert example online uses.
- **A separate port.** Metrics name every route and status code, and they are expensive to compute
  under a flood of requests. `RequireHost("*:9464")` makes `/metrics` answer only on the second port,
  which compose keeps inside its network; on the main port it is a 404, so a Cloudflare Tunnel or load
  balancer never publishes it. `MonitoringTests` checks both sides.
- **No double counting.** If metrics were both pushed and scraped, a backend that receives both would
  show every request twice. So with Prometheus on, the OTLP exporter carries only traces and logs.

**Alternatives.** *Push metrics over OTLP to the collector, and let the collector expose them to
Prometheus*: one exporter in the app, but metrics then pass through one more hop and `up` describes
the collector, not the app. *prometheus-net* (a separate library): mature, but it duplicates what
OpenTelemetry already measures, with different names.

**Trade-offs.** The OpenTelemetry Prometheus exporter for ASP.NET Core is still a **beta** package
(1.20.1-beta.1) after years, because the specification for the Prometheus format in OpenTelemetry is
not final. It is widely used and stable in practice, but a minor version could rename things. We
accepted that because the alternative (an extra hop through the collector) is worse for a small box.

## 6. The monitoring stack: a collector, then one store per signal

**The choice.** The `monitoring` profile runs the OpenTelemetry Collector, Prometheus, Alertmanager,
Tempo (traces), Loki (logs) and Grafana, with data sources and a dashboard provisioned from files.

```text
api ──OTLP──▶ otel-collector ──▶ tempo (traces)
                             └─▶ loki  (logs)
api :9464 ◀── prometheus ──▶ alertmanager
grafana reads all four
```

**Why.**
- **A collector in the middle.** The app speaks only OTLP to one address. Where the data goes is the
  collector's job: change Loki for something else, add sampling, or send a copy to a hosted backend,
  without rebuilding the app.
- **One store per signal.** Each store is built for its data: Prometheus for numbers over time, Tempo
  for traces by id (it indexes almost nothing, so it is cheap), Loki for logs indexed by labels only.
  The alternative, one store for everything (Elasticsearch), needs far more memory, which matters on
  a Raspberry Pi.
- **Correlated.** Logs already carry trace ids (R01). The Grafana data sources are provisioned with
  links both ways: a span opens its logs, a log line opens its trace. That jump is most of the value of
  having all three.
- **Provisioned, not clicked.** Data sources and dashboards load from `monitoring/grafana/` and cannot
  be changed in the UI, so the repository is the source of truth and a fresh machine looks the same.
- **Profile, not default.** About 1 GB of memory at rest. It is opt-in in compose and in the template,
  and `OTEL_EXPORTER_OTLP_ENDPOINT` still lets you send to Grafana Cloud or Honeycomb instead.

**Alternatives.** *Jaeger* for traces: good, but a separate UI; Tempo lives inside Grafana with the
rest. *Seq*: an excellent log UI, but free only for one user. *The ELK stack*: heavy. *SigNoz or
OpenObserve* (one binary for all signals): attractive and simpler; less known, and some features
sit behind their commercial editions. *The Aspire dashboard*: perfect in development (R04 already
uses it), but it keeps data in memory only and has no alerts.

**Trade-offs.** Grafana, Tempo and Loki are **AGPL 3.0**. The AGPL obliges you to share source only
if you *modify* the program and let others use it over a network. Running the official images
unchanged as separate services, as here, puts no obligation on your code, which never links to them.
We wrote that down in `docs/monitoring.md` so nobody has to wonder. Tempo 3 also logs scheduler noise
("no jobs found") in single-binary mode; it is harmless.

## 7. Four alerts, on symptoms, with tests

**The choice.** `alerts.yml` has four rules: `ApiDown`, `HighErrorRate` (more than 5% 5xx),
`SlowResponses` (p95 over 1 s) and `ThreadPoolStarved`. `alerts.test.yml` tests them with promtool,
in CI.

```yaml
# templates/ek-api/monitoring/prometheus/alerts.yml (trimmed)
- alert: HighErrorRate
  expr: |
    sum by (job) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
      / sum by (job) (rate(http_server_request_duration_seconds_count[5m])) > 0.05
  for: 5m
```

**Why.** Good alerts fire on **symptoms users feel** (down, errors, slow), not on causes (CPU at
80%), because causes are noisy and a high CPU that nobody notices is not an emergency. The fourth one
is the exception, on purpose: thread pool starvation is the classic .NET failure (sync-over-async,
`.Result` on a task), and its symptom, everything getting slow at once, is hard to diagnose without
it. `for:` durations stop a single bad scrape from paging anyone.

Alert rules are code, so they get tests: promtool feeds a made-up series and checks the alert fires
(or does not) at the expected time. Without this, the first time you learn a rule has a typo is the
outage it was meant to catch.

Alertmanager starts with a receiver that sends nothing, with commented examples. A template cannot
know your Slack, and an alert that emails nobody is better than one that emails a placeholder.

**Alternatives.** *Grafana alerting* instead of Prometheus rules: one UI, but the rules live in
Grafana's database unless you provision them, and they are harder to test in CI. *More rules*
(memory, GC, disk): easy to add once you know your normal; too many on day one teaches people to
ignore alerts.

**Trade-offs.** Thresholds (5%, 1 s) are guesses for a typical API. They are in one file with tests
next to them, so changing them is cheap.

## 8. k6 for load testing, with thresholds that fail CI

**The choice.** `loadtest/notes.js` is a k6 script: each virtual user creates, reads and lists
notes. Thresholds fail the run on more than 1% errors, a p95 over 500 ms, or failed checks. CI runs it
against the production container on every push (10 users, 20 s).

```js
// templates/ek-api/loadtest/notes.js (trimmed)
thresholds: {
  http_req_failed: ['rate<0.01'],
  http_req_duration: ['p(95)<500'],
  checks: ['rate>0.99'],
},
```

**Why.**
- **k6.** Scripts are JavaScript, which most teams can read; it runs from one container with no
  install; and thresholds turn a load test into a pass or fail check, which is what CI needs.
- **Against the container, not `dotnet run`.** The Release build, the real Kestrel settings and a
  real database are what production runs. Measuring a Debug build measures the debugger.
- **Rate limiting off for the run.** One client from one IP hits the per-caller limit (R01) in a
  second, and then you are measuring 429s. `RATE_LIMITING_ENABLED=false` in `.env` turns it off for the
  run, and the docs say to turn it back on.
- **A real, honest number.** `docs/load-testing.md` gives what we measured: about 877 requests per
  second at a p95 of 40.6 ms, 0% failed, 20 users for 30 s, on a 4 vCPU cloud machine with PostgreSQL
  in Docker on the same machine. It says what the number depends on, because a number without its
  conditions is marketing.
- **p95, not the average.** An average of 20 ms can hide 5% of users waiting two seconds. The 95th
  percentile is what a noticeable share of users actually feel.

**Alternatives.** *JMeter*: powerful but XML and a GUI. *Gatling*: Scala/Java DSL. *NBomber*: C#, which
is tempting for a .NET template, but its free licence is for personal use only, which breaks our
licence rule. *bombardier or wrk*: great for one URL, no scenarios or thresholds. *Locust*: Python,
heavier per user.

**Trade-offs.** k6 is AGPL, like Grafana: it is a tool you run, not a library you ship, so it puts no
obligation on your code. The CI run is a *smoke* load test: 20 seconds on a shared runner catches a
change that makes the API much slower or breaks it under concurrency, not a 10% regression. With
sign-in, CI measures only the health check, because a real token needs a user; the docs say how to
pass one.

## 9. Three security scans, each catching what the others miss

**The choice.** `security.yml` runs a vulnerable-package check and gitleaks over the whole history;
`codeql.yml` runs CodeQL on C# and the workflows. All run on pushes to `main`, pull requests and every
Monday.

```yaml
# templates/ek-api/.github/workflows/security.yml (trimmed)
dotnet package list --vulnerable --include-transitive --format json > vulnerable.json
if jq -e '[.projects[].frameworks[]? | (.topLevelPackages // [])[], (.transitivePackages // [])[]] | length > 0' vulnerable.json > /dev/null; then
  exit 1
fi
```

**Why.** Each scan sees a different kind of mistake:

- **Vulnerable packages**: code you did not write. *Transitive* matters most, because most
  vulnerable packages are ones you never referenced. The build's NuGet audit already warns on restore,
  but a scheduled job catches an advisory published on a quiet week when nobody builds. The JSON plus
  `jq` check is used because `dotnet package list` exits 0 even when it finds something.
- **Secrets**: credentials committed by accident. gitleaks scans *every commit* (`fetch-depth: 0`),
  because a key deleted in a later commit is still in the history and still leaked. The doc's first
  instruction for a finding is *rotate the secret*, because rewriting history does not un-leak it.
- **CodeQL**: mistakes in your own code (injection, path traversal, weak crypto). It follows data
  from a request to a dangerous call, which a linter cannot do. Scanning `actions` too catches
  workflow injection (`${{ github.event.issue.title }}` in a `run:`), a common way repositories get
  taken over.

The weekly schedule exists because new advisories appear while the code stands still.

**Alternatives.** *GitHub's own secret scanning and push protection*: better at known provider keys
and blocks the push, so the doc says to turn it on as well; but it is free only for public
repositories, and gitleaks runs anywhere. *TruffleHog*: verifies found keys against the provider,
which is great but sends your secrets to the provider's API. *Snyk or Mend*: commercial.
*Semgrep*: a good CodeQL alternative with simpler rules; CodeQL's data-flow analysis for C# is deeper.

**Trade-offs.** CodeQL on a private repository needs GitHub Code Security (paid); the doc says to
delete `codeql.yml` in that case, since the other two need no plan. `security-extended` finds more and
reports a few more false positives. gitleaks needs an allowlist for test values; ours is narrow (the
`.env.example` file, and the Standard Webhooks example secret only when webhooks are on).

## 10. Localization: translate at the edge, keep code in English

**The choice.** Every project answers problem responses in the caller's language, English or
Turkish. Code keeps writing English; `Messages.Translate` runs on every problem response and looks the
English text up in `Messages.resx`, then returns the `Messages.tr.resx` entry, keeping placeholder
values.

```csharp
// templates/ek-api/src/MyProject.Api/Resources/Messages.cs (trimmed)
var regex = Regex.Replace(Regex.Escape(text), @"\\\{(\d+)}", match =>
{
    placeholders.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    return "(.+?)";
});
// "The {0} field is required." becomes ^The (.+?) field is required\.$
```

**Why.** Most of the text a client sees is not ours: "Not Found" comes from ProblemDetails, "The
Title field is required." from DataAnnotations validation, "Too Many Requests" from the rate limiter.
The usual approach, `IStringLocalizer` everywhere, cannot reach those without replacing each
framework piece. Translating at one point, in `CustomizeProblemDetails`, catches all of them, and our
own messages too. Matching by pattern means a feature author writes `TypedResults.Problem(title:
"Quota reached")` as usual and adds a resource entry; nothing else changes.

Two tests keep it honest: every language has every key with the same placeholders, and the English
entries match what DataAnnotations writes, so a .NET upgrade that rewords a message fails a test
instead of silently showing English to Turkish users.

`Accept-Language` is the only culture source. Cookie and query-string providers make sense for web
pages, not for an API, where the client already says what it wants on every request. The response
carries `Content-Language` so the client knows what it got.

**Alternatives.** *Keys in code* (`localizer["NoteDuplicateTitle"]` in every endpoint): explicit, but
noisy, and the framework messages still need the pattern trick. *Error codes only, translated by the
client*: the best design for a large product with many clients (and our problems carry a type for
that), but every client must ship translations. *JSON or PO files* instead of `.resx`: nicer to edit,
but need a third-party localizer; `.resx` is built into the SDK and tooling.

**Trade-offs.** Matching by regex costs a little CPU on error responses only, and two English messages
that differ only in a placeholder could match the wrong entry; sorting patterns longest first and
anchoring them (`^…$`) prevents the cases we have. Only problem responses are translated, not note
content, which is user data.

## 11. Only the UI culture changes: the Turkish i

**The choice.** The request culture sets only `CurrentUICulture` (which language messages are in).
`CurrentCulture` (formatting, parsing, comparison) stays invariant for every request.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Localization.cs (trimmed)
options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo(settings.Value.DefaultLanguage));
options.SupportedCultures = [CultureInfo.InvariantCulture];
options.SupportedUICultures = [.. settings.Value.Languages.Select(language => CultureInfo.GetCultureInfo(language))];
```

**Why.** This is the most famous localization bug, and it is Turkish. In the Turkish culture,
`"i".ToUpper()` is `"İ"` (dotted capital) and `"I".ToLower()` is `"ı"` (dotless). Any code that
compares or normalizes strings with the current culture, such as `title.ToLower() == "invoice"` or a
case-insensitive search, stops matching for Turkish callers only. If the API switched the formatting
culture per request, a Turkish `Accept-Language` would change how the API *behaves*, not just how it
*speaks*. It would also change number and date parsing (`1,5` vs `1.5`). `LocalizationTests` checks
that a Turkish caller's search still finds a note with an `I` in it.

**Alternatives.** *Set both cultures*, the ASP.NET Core default: right for server-rendered pages that
show dates and numbers, wrong for an API that returns ISO dates and JSON numbers anyway.

**Trade-offs.** If you later render something for people (a PDF report, an email) with numbers or
dates, format it explicitly with the user's culture there. That is deliberate: formatting for humans
should be a visible decision at the place that does it.

## 12. Feature flags with Microsoft.FeatureManagement

**The choice.** Microsoft.FeatureManagement.AspNetCore (MIT), with flags in the `FeatureManagement`
configuration section. Names live in `FeatureFlags`. The notes export is gated as the example, and
answers 404 while `NoteExport` is off. With sign-in, targeting uses the user's id and roles.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/FeatureFlags.cs (trimmed)
builder.Services.AddFeatureManagement().WithTargeting<UserTargetingContextAccessor>();
...
UserId = user?.FindFirstValue(ClaimTypes.NameIdentifier),
Groups = user?.FindAll(ClaimTypes.Role).Select(role => role.Value).ToArray() ?? [],
```

**Why.** Flags separate *deploying* code from *releasing* a feature: ship it off, turn it on for
admins, then 10% of users, then everyone, and turn it off in seconds if it misbehaves. They are also
operations switches: the export streams every note, and `NoteExport` lets you stop it during an
incident without a deploy.

Configuration-backed flags need no server: `appsettings.json` (reloaded on change), environment
variables and user secrets all work, and Azure App Configuration plugs in later without code changes.
The built-in filters cover percentage, time windows and targeting. Targeting is "sticky": the same
user always gets the same answer, so a feature does not flicker between requests.

**404, not 403.** A gated endpoint that is off behaves as if it does not exist, which is the truth
from the client's point of view and reveals nothing about unreleased features.

**Alternatives.** *OpenFeature* (CNCF, a vendor-neutral API): the right abstraction if you plan to
switch providers; it has a Microsoft.FeatureManagement provider, so moving later is possible. *Unleash
or Flagsmith* (open-source servers): a UI and audit trail, but one more service to run. *LaunchDarkly*:
excellent, commercial. *`if (configuration["Features:X"] == "true")`*: fine for one flag, no targeting
or percentage, and no standard place to look.

**Trade-offs.** Every flag is a branch you must test both ways. The doc asks to remove release flags
once they are fully on; long-lived flags are for operations switches only. Flags in `appsettings.json`
are per instance: changing one on a single machine is instant, on many it needs a shared store (Azure
App Configuration or a flag server).

## 13. Catalog: a three-way cache choice, and the presets

**The choice.** `--cache` is a choice (`none`, `memory`, `redis`), not two booleans. `--monitoring` is
a boolean. Neither has a catalog rule. Localization, flags, k6 and scanning are in every project, with
no option.

**Why.** "Redis without the cache" makes no sense, so one choice with three values cannot express a
contradiction that two flags could. Neither option needs anything else: memory caching works without a
database, and monitoring works with any combination. The four features without an option cost almost
nothing when unused and are things every API should have, which was the R01 rule for "core".

Presets were updated: `saas-api` gets `--cache redis --monitoring` (several instances, a real product),
`api-worker` gets `--monitoring` (two processes to watch), `single-box` and `realtime` get
`--cache memory` (one machine, no Redis to run). `ek matrix` now picks 20 combinations instead of 17,
to cover the new value pairs.

**Trade-offs.** Every project now carries a k6 script, two scanning workflows and two `.resx` files.
They are small and documented; a team that does not want Turkish deletes one file and one setting.

## 14. Gotchas worth remembering

- **The template engine evaluates `A && (B || C || D)` wrongly.** It leaked Aspire packages into
  projects without Aspire. Writing the same condition as `(A && B) || (A && C) || (A && D)` works. If a
  condition with parentheses behaves oddly, expand it.
- **A new advisory can break a build overnight.** During R08, System.Formats.Cbor 10.0.14 and then
  Aspire 13.6.4 got advisories, and warnings-as-errors failed the build with no code change. That is the
  NuGet audit doing its job: we moved to 10.0.15 and 13.6.5. Expect this, and keep the weekly scan.
- **Values that only look like secrets.** The Standard Webhooks spec publishes an example secret that
  the webhook tests use; gitleaks flags it. The allowlist entry is itself conditional on `--webhooks`,
  or projects without webhooks would carry a webhook string (and fail the leftover check).
- **Tempo 3 removed the `compactor` block** from its configuration; a Tempo 2 config fails to start.
  Retention is the 14-day default.
- **Rate limits and load tests.** A load test without turning the limiter off measures the limiter.
- **`dotnet package list --vulnerable` exits 0** when it finds vulnerabilities; read its JSON.
- **Private static fields need the `_` prefix** under the naming rule from R01 (`_tags`, `_patterns`).

## Try it

```bash
# A project with Redis and monitoring
dotnet new ek-api -n Shop --db postgres --cache redis --monitoring true -o Shop && cd Shop
cp .env.example .env    # set POSTGRES_PASSWORD and CACHE_PASSWORD; add RATE_LIMITING_ENABLED=false
docker compose --profile monitoring up -d --build

# Load it, then look at it
docker run --rm -i --network host grafana/k6:2.4.0 run - < loadtest/notes.js
# Grafana at http://localhost:3000 (admin / GRAFANA_ADMIN_PASSWORD): open "API overview",
# then Explore > Tempo > Search, pick a trace and follow "Logs for this span".

# Watch the cache: the second read is served without a database query (Tempo shows no db span)
curl -s -X POST localhost:8080/v1/notes -H 'Content-Type: application/json' -d '{"title":"a","content":"b"}'

# Turkish errors
curl -s -H 'Accept-Language: tr' localhost:8080/v1/notes/00000000-0000-0000-0000-000000000000 -i | grep -i 'content-language\|title'

# Turn the export off without a deploy
docker compose exec api printenv | grep FeatureManagement   # then set FeatureManagement__NoteExport=false and restart

# Run the alert tests and a secret scan
docker run --rm -v ./monitoring/prometheus:/p --entrypoint promtool prom/prometheus:v3.9.1 test rules /p/alerts.test.yml
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:v8.30.1 git /repo --config /repo/.gitleaks.toml --redact
```

## Further reading

- HybridCache: https://learn.microsoft.com/aspnet/core/performance/caching/hybrid
- Output caching: https://learn.microsoft.com/aspnet/core/performance/caching/output
- Valkey and the Redis licence change: https://valkey.io/blog/ (and https://redis.io/legal/licenses/)
- StackExchange.Redis basics (sharing the multiplexer): https://stackexchange.github.io/StackExchange.Redis/Basics
- OpenTelemetry .NET exporters: https://opentelemetry.io/docs/languages/dotnet/exporters/
- Google SRE book, "Monitoring Distributed Systems" (symptoms vs causes, the four golden signals): https://sre.google/sre-book/monitoring-distributed-systems/
- Prometheus alerting rule tests: https://prometheus.io/docs/prometheus/latest/configuration/unit_testing_rules/
- k6 thresholds: https://grafana.com/docs/k6/latest/using-k6/thresholds/
- gitleaks: https://github.com/gitleaks/gitleaks · CodeQL: https://codeql.github.com/docs/
- ASP.NET Core localization: https://learn.microsoft.com/aspnet/core/fundamentals/localization
- The Turkish i problem: https://learn.microsoft.com/dotnet/standard/base-types/best-practices-strings
- Feature management in .NET: https://learn.microsoft.com/azure/azure-app-configuration/feature-management-dotnet-reference
- Martin Fowler, "Feature Toggles": https://martinfowler.com/articles/feature-toggles.html
