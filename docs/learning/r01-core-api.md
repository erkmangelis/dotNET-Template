# R01, Core API (0.1.0): why the base is built like this

R01 is the foundation every later release stands on: a `dotnet new` template package with one
template, `ek-api`, that generates a hardened ASP.NET Core HTTP API on .NET 10. It had three
switches (`--auth none|identity`, `--db none|postgres`, `--agents none|shared|claude`), an example
feature called Notes, tests, containers and CI. Most of what it contains is not features but
*defaults*: the settings and checks a team forgets until a review or an incident reminds them.

This doc goes through those defaults one by one. If you only read one learning doc, read this one,
because later releases reuse every idea in it.

## Contents

1. [Delivering it as a `dotnet new` template](#1-delivering-it-as-a-dotnet-new-template)
2. [How the template engine works: symbols and `#if`](#2-how-the-template-engine-works-symbols-and-if)
3. [The build: SDK pin, central packages, analyzers](#3-the-build-sdk-pin-central-packages-analyzers)
4. [One project with feature folders, not Clean Architecture (yet)](#4-one-project-with-feature-folders-not-clean-architecture-yet)
5. [Minimal APIs and typed results](#5-minimal-apis-and-typed-results)
6. [Options validated on start, secrets out of the repo](#6-options-validated-on-start-secrets-out-of-the-repo)
7. [ProblemDetails for every error](#7-problemdetails-for-every-error)
8. [Validation and mass assignment](#8-validation-and-mass-assignment)
9. [OpenAPI with Scalar](#9-openapi-with-scalar)
10. [Security defaults: headers, HTTPS, CORS, proxies, rate limits](#10-security-defaults-headers-https-cors-proxies-rate-limits)
11. [`TimeProvider` and version 7 GUIDs](#11-timeprovider-and-version-7-guids)
12. [Observability: logs, traces, metrics, health](#12-observability-logs-traces-metrics-health)
13. [PostgreSQL and EF Core](#13-postgresql-and-ef-core)
14. [Owner-only data and 404 instead of 403](#14-owner-only-data-and-404-instead-of-403)
15. [Tests in the generated project](#15-tests-in-the-generated-project)
16. [Testing the template itself](#16-testing-the-template-itself)
17. [Containers, compose and the Raspberry Pi](#17-containers-compose-and-the-raspberry-pi)
18. [Supply chain: Dependabot and signed provenance](#18-supply-chain-dependabot-and-signed-provenance)
19. [`AGENTS.md`, `CLAUDE.md` and `template-origin.json`](#19-agentsmd-claudemd-and-template-originjson)
20. [The licence rule](#20-the-licence-rule)

---

## 1. Delivering it as a `dotnet new` template

**The choice.** The template ships as a NuGet package (`Erkmangelis.Templates.0.1.0.nupkg`) that
`dotnet new install` understands. You generate a project with `dotnet new ek-api -n RideSense`.

**Why.** There are three common ways to start projects from a shared base:

| Approach | How it works | Problem |
| --- | --- | --- |
| Starter repository | Clone or "Use this template" on GitHub, then rename things by hand | Every option is either in or out for everyone. Renaming is manual and error-prone. |
| Framework (ABP and similar) | Your project depends on a large runtime framework | You learn the framework's way of doing everything; leaving it is expensive. |
| Code generator | A tool writes plain code you then own | You need a way to pick options and keep the output correct. |

`dotnet new` is the third kind, and it is built into the SDK every .NET developer already has.
The output is ordinary code with no dependency on the template, the project name is replaced
everywhere (namespaces, folders, files), and options remove code you did not ask for instead of
switching it off at runtime.

**Alternatives.** Yeoman or Cookiecutter (another runtime to install), a starter repo (no options),
ABP or FullStackHero (a framework, not a template). Copier comes up again in R05, because it solves
the one thing `dotnet new` does not: updating a project after it was generated.

**Trade-offs.** `dotnet new` cannot reject a bad combination of options and cannot update an
existing project. R05 adds the `ek` tool on top for exactly those two gaps.

## 2. How the template engine works: symbols and `#if`

The template lives in `templates/ek-api`. Its settings are in
`templates/ek-api/.template.config/template.json`. Two mechanisms do almost all the work:

- **`sourceName`.** Every occurrence of `MyProject` (in file names, folder names and file
  contents) becomes the name you pass with `-n`. That is why the source tree is full of
  `MyProject.Api`.
- **Symbols.** Each switch is a *parameter* symbol, and *computed* symbols combine them. Code then
  uses them in `#if` blocks that the engine evaluates at generation time, so unselected code never
  reaches the output.

```jsonc
// templates/ek-api/.template.config/template.json (today's version; R01's was smaller)
"UsePostgres": {
  "type": "computed",
  "value": "(db == \"postgres\" || (db == \"none\" && (auth != \"none\" || jobs == \"hangfire\" || worker || storage == \"s3\")))"
}
```

```csharp
// templates/ek-api/src/MyProject.Api/Program.cs
builder.AddApiDefaults();
builder.AddObservability();
#if (UseDatabase)
builder.AddDatabase();
#endif
```

**Why computed symbols.** In R01, `--auth identity` needs somewhere to store users. Instead of
failing when someone asks for `--auth identity --db none`, the computed `UsePostgres` turns
PostgreSQL on. Code asks "is there a database?" (`UseDatabase`) rather than repeating the rule in
every file, so the rule lives in one place.

**Why `#if` in C# and `<!--#if -->` in XML.** The engine understands the comment syntax of each
file type. In `.props` files you saw `<!--#if (UseSmtp) -->`; in C#, `#if (UseSmtp)`. The template
source still compiles as a normal project for most combinations, which makes it easy to edit.

**A trap that shaped later work.** A leftover `#if` or a stray `MyProject` in the output is a bug.
The acceptance test (section 16) greps for both.

## 3. The build: SDK pin, central packages, analyzers

```json
// templates/ek-api/global.json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

**.NET 10.** It is the current LTS (long-term support, three years of patches). A template is a
starting point for projects that live for years, so an STS release (18 months) would push every
generated project into an early upgrade.

**`global.json` with `latestFeature`.** Pins the major and minor SDK so a machine with an older
or preview SDK does not build differently, but accepts newer patch and feature bands so security
fixes do not need an edit.

**Central Package Management (`Directory.Packages.props`).** Every package version is in one file;
project files only say `<PackageReference Include="X" />`. Without it, the API and the test project
can drift onto different versions of the same package, and Dependabot has to edit several files
per update.

**Analyzers and code style in the build** (`templates/ek-api/Directory.Build.props`):

```xml
<AnalysisLevel>latest-recommended</AnalysisLevel>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<TreatWarningsAsErrors Condition="'$(CI)' == 'true' Or '$(ContinuousIntegrationBuild)' == 'true'">true</TreatWarningsAsErrors>
```

`.editorconfig` rules (naming, usings, `var`) only matter if something enforces them. Here the
build does. Warnings are errors **in CI only**, so a developer can keep working with a warning
locally but cannot merge it. The alternative, warnings as errors everywhere, makes quick local
experiments painful; no enforcement at all lets warnings pile up until nobody reads them.

## 4. One project with feature folders, not Clean Architecture (yet)

**The choice.** One API project, organised by feature:

```text
src/MyProject.Api/
  Common/          small helpers any feature may use (paging, ETags, sorting)
  Data/            EF Core context and entity configuration
  Features/Notes/  everything about notes: endpoints, contracts, store, authorization
  Infrastructure/  cross-cutting setup: ApiDefaults, Observability, SecurityHeaders
  Program.cs
```

**Why.** Clean Architecture (Domain, Application, Infrastructure, Web projects) is the most popular
.NET template style (Jason Taylor's, Ardalis'). It enforces dependency direction with project
references, which helps large teams. For a small API it mostly adds ceremony: four projects, a
mapping layer, interfaces with one implementation, and a change to one feature touches four
folders. Feature folders keep a change in one place: to understand Notes you open
`Features/Notes/`.

**Alternatives.** Clean Architecture, vertical slices with MediatR, modular monolith.

**Trade-offs.** Nothing *stops* a feature from reaching into another feature's internals. That is
acceptable at this size; R09 is planned to add layered and modular layouts as options, for
projects that grow into them. It also shows up in R04: the worker project has to reference the
whole API project, because there is no separate "core" project yet.

## 5. Minimal APIs and typed results

```csharp
// templates/ek-api/src/MyProject.Api/Features/Notes/NoteEndpoints.cs (trimmed)
var notes = routes.MapGroup("/notes").WithTags("Notes");
notes.MapPost("/", CreateAsync).WithIdempotency().WithName("CreateNote").WithSummary("Create a note");

private static async Task<Results<Created<NoteResponse>, ProblemHttpResult>> CreateAsync(
    CreateNoteRequest request, INoteStore store, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
```

**Why minimal APIs over controllers.** Microsoft's direction since .NET 6, less code per endpoint,
faster startup, and every new feature (built-in validation, OpenAPI document generation) lands
there first. Each feature exposes a `MapXxxEndpoints` extension method, so `Program.cs` reads like
a table of contents.

**Why `Results<Created<NoteResponse>, ProblemHttpResult>`.** The return type lists every possible
response. The compiler checks you only return those, and the OpenAPI generator reads them, so the
documentation cannot drift from the code.

**Alternatives.** MVC controllers (familiar, attribute-heavy, still supported), FastEndpoints (nice,
but a third-party framework you depend on), MediatR handlers (now commercially licensed, see
section 20).

## 6. Options validated on start, secrets out of the repo

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/ApiDefaults.cs
services.AddOptions<CorsSettings>()
    .BindConfiguration(CorsSettings.SectionName)
    .Validate(CorsSettings.AreValid, "Cors:AllowedOrigins must hold absolute http(s) origins without paths, and never '*'.")
    .ValidateOnStart();
```

**The options pattern.** Each settings section binds to a small class (`ApiOptions`,
`CorsSettings`, `RateLimitingOptions`), so code asks for `IOptions<CorsSettings>` instead of
reading strings like `configuration["Cors:AllowedOrigins"]` all over the place.

**Why `ValidateOnStart`.** Without it, validation runs the first time something reads the options,
which may be hours after a deploy, on the first request that needs CORS. With it, a bad setting
stops the app at startup and the deploy fails where you can see it. This is called *failing fast*.

**Secrets.** `appsettings.json` holds only safe defaults. Connection strings with passwords, API
keys and SMTP passwords come from user secrets in development and environment variables in
containers (`.env.example` lists them). A test checks that no secret reaches the logs. The reason is
simple: anything in the repository is eventually seen by someone who should not see it.

## 7. ProblemDetails for every error

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/ApiDefaults.cs
services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
});
```

**Why.** RFC 9457 ("Problem Details for HTTP APIs") is the standard JSON shape for errors:
`type`, `title`, `status`, `detail`, `instance`. Clients handle one shape for every error instead
of guessing per endpoint. `UseExceptionHandler` and `UseStatusCodePages` make even unhandled
exceptions and bare 404s use it, and they never leak a stack trace.

**Why the `traceId`.** When a user reports "I got an error", the trace id in the response is the
key to find the exact request in the logs and traces (section 12).

## 8. Validation and mass assignment

```csharp
// templates/ek-api/src/MyProject.Api/Features/Notes/NoteContracts.cs
/// What a client may send. Only these fields are bound, so a client cannot set the id,
/// the record metadata or the owner.
public sealed record CreateNoteRequest
{
    [Required]
    [StringLength(Note.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;
    ...
}
```

**Built-in validation.** .NET 10 added `services.AddValidation()` for minimal APIs: data
annotations on request types are checked before your handler runs, and failures answer 400
ProblemDetails. FluentValidation is the popular alternative, with a nicer API for complex rules,
but it is one more dependency for something the framework now does.

**Mass assignment.** If an endpoint binds the request straight into the entity (`Note`), a client
can send `"ownerId": "someone-else"` or `"createdAt": ...` and the binder happily sets them. The
fix is a *request contract*: a separate record that contains only what a client may set. A test
sends extra fields and checks they are ignored. Responses use their own contract too
(`NoteResponse`), so adding a column to the entity never leaks it by accident.

**Why not AutoMapper for entity-to-contract mapping.** The mapping is one line
(`NoteResponse.From(note)`), it is checked by the compiler, and AutoMapper is now commercially
licensed.

## 9. OpenAPI with Scalar

**The choice.** `Microsoft.AspNetCore.OpenApi` generates the OpenAPI document; Scalar renders it as
an interactive reference. Both are on in Development and off elsewhere (`Api:ExposeOpenApi`).

**Why.** Since .NET 9 Microsoft ships its own document generator and dropped Swashbuckle from its
templates (Swashbuckle had gone largely unmaintained). Scalar is a modern, MIT-licensed UI with a
built-in request client. NSwag is the other option, strong at client generation, heavier as a
dependency.

**Why off in production.** A public API description is a map for attackers too. Turn it on if your
API is public and documented anyway.

## 10. Security defaults: headers, HTTPS, CORS, proxies, rate limits

These are the items that come up again and again in reviews. Each is a few lines.

**Security headers** (`Infrastructure/SecurityHeaders.cs`):

```csharp
headers.XContentTypeOptions = "nosniff";         // the browser trusts our Content-Type
headers.XFrameOptions = "DENY";                  // nobody can frame us (clickjacking)
headers["Referrer-Policy"] = "no-referrer";      // URLs do not leak to other sites
headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
```

The API returns JSON only, so the strictest Content Security Policy fits: the browser may load
nothing from a response. The Scalar UI is exempt because it needs scripts and styles. The server
header is removed (`AddServerHeader = false`) so we do not advertise Kestrel's version.

**HTTPS redirect and HSTS.** HSTS tells browsers to use HTTPS only for this host, for a long time.
It is skipped in Development because it would stick to `localhost` in your browser.

**CORS from settings.** By default no origin is allowed. Origins are listed in configuration, and
validation refuses `*` and URLs with paths. A wildcard with credentials is a classic mistake that
lets any website call the API as the signed-in user.

**Forwarded headers with known proxies only.** Behind a reverse proxy (Cloudflare Tunnel, nginx),
the client IP and scheme arrive in `X-Forwarded-For` and `X-Forwarded-Proto`. If the app trusts
those headers from anyone, a client can fake its IP and dodge rate limits. So only listed networks
or proxies are trusted.

**Per-caller rate limiting.**

```csharp
var key = userId is not null ? $"user:{userId}" : $"ip:{context.Connection.RemoteIpAddress}";
return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { ... });
```

The built-in rate limiter (since .NET 7) partitions by signed-in user, or by IP when anonymous.
A global limit would let one noisy client lock everyone out. A *fixed window* is the simplest
algorithm; sliding windows and token buckets smooth bursts better and are a one-line change. A
rejected request answers 429 ProblemDetails with `Retry-After`.

**ASVS.** [docs/asvs-l1.md](../asvs-l1.md) maps OWASP's Application Security Verification Standard
level 1 to what the template covers and what each project still owns. It is how we know the list
above is not random.

## 11. `TimeProvider` and version 7 GUIDs

**`TimeProvider`.** Code never calls `DateTime.UtcNow`; it asks for `TimeProvider` (built into
.NET 8+). In tests, `FakeTimeProvider` lets you jump forward an hour instantly, which matters for
expiring tokens, retention and schedules (R03, R04). Before .NET 8 every project wrote its own
`IClock` interface; now the framework has one.

**`Guid.CreateVersion7`.** IDs are generated by the app, not the database, so the id is known before
saving (useful for `Location` headers and idempotency). Version 7 GUIDs start with a timestamp, so
they sort by creation time, and index inserts stay at the end of the B-tree instead of scattering
like random version 4 GUIDs. Integers would leak how many records you have and make ids guessable.

## 12. Observability: logs, traces, metrics, health

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Observability.cs (trimmed)
builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole(...);
}
var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(...).AddHttpClientInstrumentation())
```

**JSON logs.** Readable text in Development, JSON elsewhere, because log systems (Loki, Seq,
Elastic) search fields, not sentences. Every line carries the trace id.

**OpenTelemetry.** The vendor-neutral standard for traces, metrics and logs. The app exports over
OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, so it works with Grafana, Honeycomb, Jaeger,
Azure Monitor or the Aspire dashboard (R04) without code changes. The alternative, a vendor SDK
(Application Insights, Datadog), ties the code to one vendor.

**Why not Serilog.** Serilog is great and very common. The built-in logger plus OpenTelemetry now
covers structured JSON and trace correlation, so we avoid the extra dependency. Adding Serilog
later is easy if a team prefers it.

**Two health endpoints.** `/health/live` answers "is the process alive?" (restart it if not).
`/health/ready` answers "can it serve traffic?" and checks the database (stop sending it requests if
not). Mixing them is a common mistake: if the database is down, a combined check makes the
orchestrator restart healthy app containers in a loop, which helps nobody.

## 13. PostgreSQL and EF Core

**PostgreSQL 18.** Free, fast, runs on a Raspberry Pi (arm64) and in every cloud, and has features
later releases use (advisory locks in R04, `xmin` row versions in R03). SQL Server and SQLite came
in R03.

**EF Core with Npgsql.** The standard .NET ORM. It handles migrations, change tracking and LINQ
queries. Hand-written SQL with Dapper is faster for hot reads; R03 adds a recipe for that.

**Pre-generated migrations.** A template cannot ask you to run `dotnet ef migrations add` after
generating, so the first migration ships in `templates/ek-api/.migrations/` and `template.json`
copies the right set for your options. R03 made this a script.

**Migration bundle.** The Dockerfile builds `efbundle`, a single executable that applies pending
migrations, and compose runs it as a `migrator` container before the API starts. The alternative,
`Database.Migrate()` at startup, is fine for one instance but races when several instances start at
once, and the app's database user then needs schema-change rights. (Development still migrates on
startup for convenience.)

**Testcontainers, not the in-memory provider.** Integration tests start a real PostgreSQL in Docker.
EF Core's in-memory provider does not enforce unique indexes, transactions or SQL semantics, so
tests pass that would fail against the real database.

## 14. Owner-only data and 404 instead of 403

```csharp
// templates/ek-api/src/MyProject.Api/Features/Notes/NoteAuthorization.cs
internal sealed class NoteOwnerHandler : AuthorizationHandler<NoteOwnerRequirement, Note>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NoteOwnerRequirement requirement, Note resource)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && string.Equals(context.User.GetUserId(), resource.OwnerId, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
```

**Resource-based authorization.** "Is this user signed in?" is checked by a policy on the
endpoint. "Does this user own *this* note?" can only be checked after loading the note, so it is a
handler that receives the note as the resource. This is the defence against IDOR (insecure direct
object reference), the top item in OWASP's API Security Top 10: changing an id in the URL must not
show someone else's data.

**Why 404 and not 403.** 403 says "this note exists, but it is not yours", which confirms that the
id is valid. 404 reveals nothing. Every later feature (files in R06 included) follows the same
rule, and a test checks it per feature.

## 15. Tests in the generated project

**xUnit v3 on Microsoft.Testing.Platform.** The current xUnit, running on Microsoft's new test
runner (configured in `global.json`). It is faster and runs test projects as plain executables.

**Unit tests and integration tests as separate projects.** Unit tests are fast and need nothing.
Integration tests use `WebApplicationFactory` to run the whole API in memory and call it over a
real HTTP pipeline (routing, auth, validation, ProblemDetails, headers), plus Testcontainers for the
database. Most of the value is in integration tests: they test what a client sees.

**Security tests ship with the template**: forbidden access, mass assignment, other users' notes
answer 404, and no secrets in logs. A team that changes an endpoint later finds out immediately if
it broke one of these.

## 16. Testing the template itself

A template has a special testing problem: the code you want to test does not exist until you run
the template, and it is different for every combination of options. Two layers solve it.

**Acceptance test** (`scripts/acceptance.sh`), run in CI for every supported combination:

1. pack the template and generate a project *outside* the repository from the `.nupkg`, exactly as
   a user would,
2. check that no `#if` markers or `MyProject` names are left,
3. check that nothing from unselected options is left (`leftover` greps, for example no `Npgsql`
   with `--db none`),
4. restore, build with warnings as errors, run `dotnet format --verify-no-changes`,
5. run all generated tests,
6. add a deliberate style violation and check that the build fails (proving the analyzers are on),
7. with `--compose`, start it with Docker Compose and expect 200 from `/health/ready`.

```bash
# scripts/acceptance.sh
leftover() { if git grep -nIiE "$1" -- . ':!template-origin.json' ':!**/Data/Migrations/**' "${@:3}"; then fail "$2"; fi; }
```

**Snapshot tests** (`tests/Erkmangelis.Templates.Tests`, with Microsoft's TemplateVerifier):
every generated file for each combination is stored under `Snapshots/`. Any change to the output
shows up as a diff in the pull request, so you review *what users will get*, not just the template
source. `scripts/accept-snapshots.sh` accepts an intended change.

TemplateVerifier is built on xUnit v2, which is why the template repository's own tests use v2
while generated projects use v3.

**"Supported means tested."** The approved definition for R01: a combination is supported only if
CI runs the acceptance test on it. R01 had 9 combinations and CI ran all 9. Later releases grew the
matrix until R05 had to get smarter about choosing combinations.

## 17. Containers, compose and the Raspberry Pi

```dockerfile
# templates/ek-api/Dockerfile (trimmed)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
RUN dotnet restore src/MyProject.Api/MyProject.Api.csproj -a $TARGETARCH
...
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER $APP_UID
```

**Multi-stage build.** The SDK image (large) compiles; the final image holds only the runtime and
the published app. It is smaller and has no compiler for an attacker to use.

**Multi-arch with `$BUILDPLATFORM`.** The build stage runs on the build machine's native
architecture and *cross-publishes* for the target (`-a $TARGETARCH`). Building arm64 images under
QEMU emulation works too but is many times slower. arm64 matters because the target host is a
Raspberry Pi.

**Non-root user.** `USER $APP_UID` uses the non-root user Microsoft's images provide. If the app is
compromised, the attacker is not root in the container.

**Docker Compose, not Kubernetes.** The approved deployment target is a single Pi or VPS. Compose
runs the API, the database and the migrator with one file and no cluster to operate. Kubernetes
pays off with many services and machines; for one box it is mostly overhead.

**HTTPS through Cloudflare Tunnel.** A tunnel connects outward from the Pi to Cloudflare, so you
open no port on your home router, need no static IP, and get a certificate automatically. The
alternative, port forwarding plus Caddy or nginx with Let's Encrypt, exposes your home IP and needs
a reachable port 80/443. It is a compose profile (`tunnel`), so it starts only when asked.

## 18. Supply chain: Dependabot and signed provenance

**Dependabot** in both the template repository and every generated project. Most vulnerabilities
in a typical app come from outdated packages, and nobody updates them by hand reliably.

**Build provenance attestation.** The release workflow (and the generated container workflow) uses
GitHub's attestation action to sign a statement that says "this file was built from this commit by
this workflow". Anyone can verify it with `gh attestation verify`. It protects against a package or
image that was swapped or built somewhere else. This came out of the research on what large
companies do (SLSA-style provenance).

## 19. `AGENTS.md`, `CLAUDE.md` and `template-origin.json`

**`--agents`.** `AGENTS.md` is an emerging convention: one file that tells AI coding agents how
this project builds, tests and is organised. The template writes it *for the options you chose*
(no SQL Server notes in a PostgreSQL project). `--agents claude` adds a `CLAUDE.md` that imports it,
because Claude Code reads that name. `shared` is the default because the file is useful to humans
too.

**`template-origin.json`.** Records the template version and the options used. It costs nothing at
generation time and is what makes R05's `ek update` possible: to update a project you need to know
exactly what it was generated from.

## 20. The licence rule

Standing rule, decided before R01: **no default dependency may need a commercial licence.** In
2024 and 2025 several popular .NET libraries moved to paid licences for commercial use: MediatR,
AutoMapper, MassTransit v9, FluentAssertions v8, and Duende IdentityServer was already paid. A
template that includes them hands every generated project a bill or a migration.

The rule is enforced by a test, so it cannot be forgotten:

```csharp
// tests/Erkmangelis.Templates.Tests/TemplatePackageTests.cs
string[] banned = ["MediatR", "AutoMapper", "MassTransit", "FluentAssertions", "Duende."];
```

You will see this rule decide library choices in R04 (Hangfire, but never Hangfire Pro) and most
visibly in R06 (PDF and Excel libraries).

---

## Try it

1. Generate two projects, `--db none` and `--db postgres`, and diff them. Every difference is an
   `#if` block or a conditional file in `template.json`.
2. Set `Cors:AllowedOrigins` to `["*"]` in `appsettings.Development.json` and start the API. It
   refuses to start: that is `ValidateOnStart`.
3. Call a note that does not exist and look at the response: a ProblemDetails body with a
   `traceId`. Find that trace id in the console log.
4. Run `scripts/acceptance.sh --auth none --db none --agents none` and read each step's output.

## Further reading

- `dotnet new` custom templates: https://learn.microsoft.com/dotnet/core/tools/custom-templates
- RFC 9457, Problem Details: https://www.rfc-editor.org/rfc/rfc9457
- OWASP API Security Top 10: https://owasp.org/API-Security/
- OpenTelemetry for .NET: https://opentelemetry.io/docs/languages/dotnet/
