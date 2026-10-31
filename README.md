# Erkmangelis .NET templates

`dotnet new` templates for .NET 10, and `ek`, a tool that generates and updates projects from them.
Release 0.6 has one template: **`ek-api`**, a hardened ASP.NET Core HTTP API that fixes the
security, testing and secrets gaps that come up again and again in project reviews.

```bash
dotnet new install Erkmangelis.Templates.0.6.0.nupkg
dotnet new ek-api -n RideSense --auth identity --db postgres --agents claude
```

Or with the tool, which asks what you need, offers presets and rejects unsupported combinations:

```bash
dotnet tool install --global Erkmangelis.Ek --add-source <folder with Erkmangelis.Ek.0.6.0.nupkg>
ek new RideSense --preset saas-api
ek update        # later, inside the project: merge a newer template version's changes
```

New here? Follow [docs/getting-started.md](docs/getting-started.md). The tool: [docs/cli.md](docs/cli.md).
Why it is built this way, release by release: [docs/learning](docs/learning/README.md).

## Options

| Option | Values | Default | Adds |
| --- | --- | --- | --- |
| `--auth` | `none`, `identity`, `external`, `oidc` | `none` | `identity`: ASP.NET Core Identity accounts with bearer tokens or same-site cookie sessions with antiforgery, lockout, email confirmation and reset, authenticator-app MFA, passkeys. `external`: the same accounts plus Google, Apple and Microsoft sign-in, linked only on a provider-verified email. `oidc`: no local accounts; the API trusts access tokens from one OpenID Connect provider, with a development Keycloak and an enterprise SSO (SAML) recipe. Every mode adds roles and permissions, admin endpoints, hashed and scoped API keys, user profiles and an owner-only policy on the example feature, and turns PostgreSQL on when `--db` is `none`. |
| `--db` | `none`, `postgres`, `sqlserver`, `sqlite` | `none` | EF Core with migrations, record metadata, soft delete, audit history, a migration bundle, compose and a backup script. `postgres`: PostgreSQL 18 through Npgsql. `sqlserver`: SQL Server 2022 (Express by default; you accept its licence in `.env`; amd64 only). `sqlite`: one file on a volume, no database server. PostgreSQL and SQL Server tests use Testcontainers. A Dapper recipe covers hand-written SQL for hot read paths. |
| `--email` | `none`, `smtp` | `none` | Templated confirmation and reset emails through SMTP (MailKit), Mailpit for local testing. With `none`, emails are only logged, in development. |
| `--jobs` | `none`, `hosted`, `hangfire` | `none` | Background jobs: queued jobs (`IJob<TArgs>` through `IJobQueue`) and cron-scheduled recurring jobs, bounded retries with backoff, failed-job listing and requeue for admins, idempotent job effects (`JobIdempotency`) and distributed locks (PostgreSQL advisory locks, SQL Server app locks, lock files with SQLite). `hosted`: an in-process queue and cron schedules on hosted services; queued jobs are lost on restart. `hangfire`: Hangfire (LGPL-3.0) storing jobs in the database, so they survive restarts, with a dashboard behind the `jobs.manage` permission; turns PostgreSQL on when `--db` is `none`. Every choice has the scoped background loop for housekeeping. |
| `--storage` | `none`, `s3` | `none` | Private files on any S3-compatible storage (RustFS in compose, Cloudflare R2 or Amazon S3 in production): uploads through the API, signed upload and download links, and resumable tus uploads; size limits, type checks by file signature, optional ClamAV scanning, SHA-256, WebP thumbnails (SkiaSharp), owner-only access and a cleanup loop for temporary, unfinished, refused and orphaned files. Turns PostgreSQL on when `--db` is `none`. |
| `--documents` | `false`, `true` | `false` | CSV and Excel export and import of notes (MiniExcel, Apache-2.0) with formula cells defused, and a PDF report (PdfPig, Apache-2.0, with an embedded Liberation Sans font). |
| `--realtime` | `false`, `true` | `false` | Changes pushed to connected clients over SignalR (`/v1/realtime/hub`), server-sent events (`/v1/realtime/events`) and raw WebSockets (`/v1/realtime/ws`, JSON frames with a version handshake), per user with `--auth`, with an origin check against cross-site WebSocket hijacking and a bounded queue per connection. |
| `--notifications` | `false`, `true` | `false` | An in-app inbox with read status, per-user channel preferences, and delivery in the background by email (with `--email smtp`), mobile push (Firebase Cloud Messaging and Apple Push Notification service, called over HTTP) and SMS (Twilio), with retries and dead-device cleanup. Means local accounts when `--auth` is `none`. |
| `--webhooks` | `false`, `true` | `false` | Standard Webhooks in both directions: incoming with signature and timestamp checks, duplicate detection and queued handlers; outgoing subscriptions to API events with signed deliveries, retries, a delivery log and SSRF-safe sending. Turns PostgreSQL on when `--db` is `none`. |
| `--worker` | `false`, `true` | `false` | A worker project and image that runs the jobs while the API only queues them. Means `--jobs hangfire`. |
| `--aspire` | `false`, `true` | `false` | A .NET Aspire AppHost (MIT) that starts the database, the API and the worker for local development, with the Aspire dashboard. |
| `--agents` | `none`, `shared`, `claude` | `shared` | `AGENTS.md` written for the chosen options; `claude` adds a `CLAUDE.md` that imports it. |

Every `--auth` mode but `none`, `--jobs hangfire`, `--storage s3`, `--notifications` and `--webhooks` need a
database, so with `--db none` they generate the PostgreSQL output; `--notifications` means `--auth identity`
when `--auth` is `none`; `--worker` means `--jobs hangfire`. Every project gets outbound HTTP clients with
timeouts, retries, a circuit breaker and a host allowlist (Microsoft.Extensions.Http.Resilience). These rules live in
[catalog/modules.json](catalog/modules.json): `ek` applies them and rejects a combination that
breaks one, with the reason. Presets (`minimal`, `saas-api`, `api-worker` alias `ek-worker`,
`sso-api`, `single-box`, `realtime`) live in [catalog/presets.json](catalog/presets.json).

## Supported means tested

Every combination the catalog's rules accept is supported, and CI tests a set chosen from the
catalog by `ek matrix`: every preset, plus combinations that cover every pair of option values that
can occur together (17 in all, instead of the hundreds of possible combinations). For each, CI runs
the acceptance test ([scripts/acceptance.sh](scripts/acceptance.sh)):

1. generates the project outside the source tree from the packed `.nupkg`,
2. checks that no template markers and nothing from unselected options are left behind,
3. restores, builds with warnings as errors and runs the format check,
4. runs all generated tests, including ownership, forbidden-access, mass-assignment, API key scope,
   no-secrets-in-logs and (with `--auth external`) unverified-email takeover tests,
5. checks that a deliberate style violation fails the build,
6. starts it with `docker compose` and expects 200 from `/health/ready`, and with `--email smtp` and
   local accounts, that registering sends a confirmation email to the bundled Mailpit, and with
   `--jobs` (or `--worker`) and no sign-in, that a background job imports notes in the containers,
   and with `--storage s3` and no sign-in, that a file uploads to the bundled storage and downloads
   again through the API and a signed link.

CI also generates projects with released versions (0.1.0, 0.3.0, 0.4.0 and 0.5.0), commits a file of
the team's own, runs `ek update` to the current version and runs the same acceptance test on the
result, and builds the API and worker images for amd64 and arm64.

Snapshot tests ([tests/Erkmangelis.Templates.Tests](tests/Erkmangelis.Templates.Tests)) pin the
generated files for each combination, so every change to the output shows up in review. Tool tests
([tests/Erkmangelis.Ek.Tests](tests/Erkmangelis.Ek.Tests)) check the catalog against the template's
symbols, that `ek new` and plain `dotnet new` give identical output for every preset, and the update
merge. `--agents` only adds or removes `AGENTS.md` and `CLAUDE.md`, which a snapshot test checks.

## What every generated API has

- .NET 10 LTS pinned in `global.json`, central package versions, recommended analyzers and naming rules enforced in the build
- Typed options validated on start; secrets only in user secrets or environment variables
- ProblemDetails for every error, with a trace id; built-in validation
- OpenAPI with the Scalar UI (Development by default), `/v1` routes, paging
- Per-caller rate limiting, CORS and forwarded headers from settings, HTTPS redirect, HSTS, security headers
- JSON logs with trace ids, OpenTelemetry traces and metrics (OTLP), live and ready health checks
- `TimeProvider`, graceful shutdown
- Unit and integration tests (xUnit v3, `WebApplicationFactory`)
- A multi-arch Docker image (amd64 and arm64), Docker Compose, and a Raspberry Pi or VPS recipe with HTTPS through a Cloudflare Tunnel
- CI, container publishing with signed provenance, and Dependabot

No default dependency needs a commercial licence: no MediatR, no AutoMapper, no Hangfire Pro.

## Working on the template

```bash
dotnet test tests/Erkmangelis.Templates.Tests        # snapshot tests
dotnet test tests/Erkmangelis.Ek.Tests               # tool tests
dotnet run --project src/Erkmangelis.Ek -- new       # the tool, from source
scripts/acceptance.sh --auth identity --db postgres --email smtp --jobs hangfire --worker true --agents claude --compose
```

Template sources live in [templates/ek-api](templates/ek-api). Conditional code uses the template
engine's `#if (UsePostgres)`-style symbols defined in
[template.json](templates/ek-api/.template.config/template.json). After an intended change to the
output, run the snapshot tests and accept the new output with `scripts/accept-snapshots.sh`, then review the snapshot diff.

Migrations ship pre-generated in `templates/ek-api/.migrations`, one set per sign-in mode. After a
change to the EF model, run `scripts/add-migration.sh <Name>` to add a migration to every set from
the current template; running it again with the newest migration's name replaces that migration.

A new option also needs an entry in [catalog/modules.json](catalog/modules.json), a line in the
template's `template-origin.json` and a regenerated [spec schema](schemas/ek-spec.schema.json); the
tool tests say which one is missing. See [docs/cli.md](docs/cli.md#maintainers).

Releases: push a `v*` tag. The release workflow packs the template and the `ek` tool, attests their
build provenance and attaches both `.nupkg` files to a GitHub release. See [CHANGELOG.md](CHANGELOG.md).

Security baseline: [docs/asvs-l1.md](docs/asvs-l1.md) maps OWASP ASVS Level 1 to what the template
covers and what each project still owns.
