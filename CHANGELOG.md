# Changelog

All notable changes to the template package. Versions follow [Semantic Versioning](https://semver.org/).

## [0.5.0] - Unreleased

R05, generator tooling.

### Added

- `ek`, a dotnet tool (`Erkmangelis.Ek`) that carries the template package of its own version:
  - `ek new` asks for the name and a preset or each option, offering only values the rules accept
    with the earlier answers, prints the equivalent `dotnet new` command and runs it in a private
    template hive. `-y`, `--spec`, `--save-spec`, `--dry-run`, `-o` and `--package`.
  - `ek update` regenerates the project at its recorded version and at the new one and three-way
    merges the difference into the project, Copier-style: unchanged files take the new version,
    changed files are merged line by line, overlapping changes get conflict markers, and nothing
    the project changed is overwritten. Needs a clean git working tree. Keeps user secrets ids.
  - `ek check`, `ek list`, `ek schema`, `ek matrix`.
- A versioned project spec (`specVersion` 1) with a JSON Schema,
  [schemas/ek-spec.schema.json](schemas/ek-spec.schema.json), generated from the catalog.
- [catalog/modules.json](catalog/modules.json): the template's options with `requires`, `excludes`
  and fallback rules, each with a reason. Tests check it against the template's real symbols and
  that its rules give the same effective options as the template's computed symbols.
- Presets in [catalog/presets.json](catalog/presets.json): `minimal`, `saas-api`, `api-worker`
  (alias `ek-worker`), `sso-api` and `single-box`.
- Generated projects: a README section on updating with `ek update`.
- CI: an `update` job that generates projects with 0.1.0, 0.3.0 and 0.4.0, adds a file of the
  team's own, runs `ek update` and the full acceptance test on the result.

### Changed

- `template-origin.json` is now a full spec: it adds `specVersion` and the project `name`, so
  `ek new --spec template-origin.json` reproduces the project.
- The CI acceptance matrix comes from `ek matrix`: every preset plus pairwise coverage of the
  option values (18 combinations, was 32 picked by hand). The release workflow also packs and
  attests the tool.

## [0.4.0] - 2026-10-30

R04, background work and the worker.

### Added

- `ScopedBackgroundLoop`: a hosted service base for repeating in-process work, with a new DI scope
  per run, `TimeProvider` timers and failures logged without stopping the loop. Idempotency key
  cleanup now runs on it.
- `--jobs none|hosted|hangfire`. Both job choices share the contracts: `IJob<TArgs>` queued through
  `IJobQueue`, and `IRecurringJob` with a cron schedule (overridable in `Jobs:Schedules`). The notes
  feature gains `POST /v1/notes/imports` (202, a job creates the notes) and a daily job that purges
  notes deleted more than 30 days ago.
  - `hosted`: a bounded in-process queue, `Jobs:WorkerCount` runners and a Cronos (MIT) scheduler.
    Queued jobs are lost on restart.
  - `hangfire`: Hangfire 1.8 (LGPL-3.0) storing jobs in PostgreSQL, SQL Server or a SQLite file
    next to the database, so a queued job survives a restart (tested). The dashboard at `/jobs`
    (`Jobs:Dashboard`) needs the new `jobs.manage` permission, or local requests without sign-in.
    Turns PostgreSQL on when `--db` is `none`.
- Retries: a failing job runs up to `Jobs:MaxAttempts` times with exponential backoff from
  `Jobs:RetryDelaySeconds`, then stays failed and is logged as an error. With sign-in, admins list
  failed jobs (`GET /v1/admin/jobs/failed`) and requeue one with all its attempts
  (`POST /v1/admin/jobs/failed/{id}/requeue`).
- `JobIdempotency.RunOnceAsync`: a job's effect happens once per key, in the same transaction as the
  work when there is a database, so a repeated or retried job has no repeated effect (tested).
- `IDistributedLock`: PostgreSQL advisory locks, SQL Server application locks, lock files next to a
  SQLite database, or an in-process lock. The hosted scheduler uses it with `JobIdempotency` so a
  recurring job runs once per scheduled time across instances.
- `--worker`: `src/MyProject.Worker` references the API project and runs the Hangfire server while
  the API only queues jobs. It has its own image stage (published for amd64 and arm64 by the
  generated container workflow) and compose service. Means `--jobs hangfire`.
- `--aspire`: a .NET Aspire 13.6 AppHost (MIT) that starts PostgreSQL or SQL Server, the API and the
  worker for local development; the dashboard receives the apps' existing OTLP telemetry.
- `template-origin.json` records `jobs`, `worker` and `aspire`.
- CI: fourteen new combinations (32 in all) and a job that builds the API and worker images for
  linux/amd64 and linux/arm64.

## [0.3.0] - Unreleased

R03, Data and API depth.

### Added

- Filtering and sorting on `GET /v1/notes` (and the admin list): `search` matches titles ignoring
  case with LIKE wildcards taken literally, `createdFrom` and `createdBefore` bound the creation
  time, and `sort` takes up to three allowlisted fields (`createdAt`, `updatedAt`, `title`), each with an optional
  `-` for descending. Anything else answers 400. `SortFields<T>` is the reusable allowlist.
- Record metadata: `CreatedAt`, `UpdatedAt` and, with sign-in, `CreatedBy` and `UpdatedBy`, filled
  in by an EF Core `SaveChangesInterceptor` (and the in-memory store) from `TimeProvider` and the
  signed-in user, never from a request. `PUT /v1/notes/{id}` replaces a note's title and content.
- `scripts/add-migration.sh` regenerates a migration for every shipped migration set.
- Soft delete: `DELETE /v1/notes/{id}` marks the note deleted (`DeletedAt`, and `DeletedBy` with
  sign-in) through the same interceptor; a named `SoftDelete` query filter hides it, and unique
  indexes cover only rows that are not deleted, so the title can be used again.
- Audit history: every change to an `IAudited` entity writes an `AuditEntry` in the same transaction
  with each changed value before and after, the actor and the trace id. `GET /v1/notes/{id}/history`
  for the owner; `GET /v1/admin/audit` for roles with the new `audit.read` permission (admin, auditor).
- Optimistic concurrency: notes carry a `version` (PostgreSQL's `xmin` through Npgsql's row version).
  An update can send the version it read; the save's `UPDATE` matches only that version, so a stale
  update or a change racing between read and write answers 409 ProblemDetails ("Edit conflict")
  instead of overwriting someone else's change.
- Idempotency keys: `.WithIdempotency()` endpoints (`POST /v1/notes`) honour `Idempotency-Key`. A
  retry with the same key and request replays the stored response (`Idempotent-Replayed: true`), a
  different request with the key answers 422, a retry while the first runs answers 409, and 5xx
  responses are not kept. Keys are per caller, stored in the database (in memory with `--db none`)
  for `Idempotency:RetentionHours` and cleaned up hourly.
- Conditional requests: notes answer with a strong `ETag` from their version; `If-None-Match` on
  `GET /v1/notes/{id}` answers 304, and `If-Match` on `PUT` and `DELETE` answers 412 when the note
  changed. `ETags` in `Common/` parses both headers for any feature.
- Streaming: `GET /v1/notes/export` streams every note matching the list's filters as one JSON array
  through `IAsyncEnumerable`, never holding the result in memory.
- API versions: Asp.Versioning (MIT) with the version in the URL, one OpenAPI document per version,
  both in the Scalar reference, and `api-supported-versions` on responses. Notes serve v1 and v2;
  `GET /v2/notes` drops the total count (`PageSlice<T>`), everything else is unchanged in v2.
  Location headers follow the requested version.
- `docs/api.md` in generated projects: the API conventions above, for clients and for new features.
- `--db sqlserver`: SQL Server 2022 through Microsoft.Data.SqlClient, with a `rowversion` for the
  note version, Testcontainers, a compose service (Express edition by default; `MSSQL_ACCEPT_EULA`
  in `.env` accepts the licence, so the template never does), and `BACKUP DATABASE` backups. The
  image is amd64 only.
- `--db sqlite`: one database file on the `app-data` volume, no database server. The note version is
  a concurrency token the store increments, `DateTimeOffset` values are stored as UTC ticks so they
  sort, integration tests use a temporary file, and backups use SQLite's online backup.
- `docs/dapper.md`: a Dapper recipe for hot read paths, for each provider, that keeps the
  soft-delete and owner filters and is checked against EF by a test.

### Changed

- Notes gain `PUT` and `DELETE`, and responses gain `updatedAt` and `version`.
- `/v1` routes are mapped through Asp.Versioning instead of a plain route group.
- The template's migration sets live under `.migrations/<provider>/`; `scripts/add-migration.sh`
  covers all nine and starts a new set with `InitialCreate`.
- CI runs eighteen combinations: every auth mode on SQL Server and SQLite as well.

## [0.2.0] - Unreleased

R02, Accounts.

### Added

- `--email none|smtp`: an email transport behind `IEmailTransport`, SMTP through MailKit with
  settings validated on start, HTML-encoded templates for confirmation and reset, a log transport
  for development only, Mailpit in compose (profile `mail`) and a Testcontainers test.
- `--auth external`: Identity accounts plus Google, Apple and Microsoft sign-in from the provider's
  ID token (audience, issuer, signature, nonce). A provider identity links to an existing account only
  when the provider says the email is verified; signing in that way to an unconfirmed account wipes
  the unproven credentials first. Linking, unlinking (never the last sign-in method) and tests for
  each path, including the unverified-email takeover.
- `--auth oidc`: no local accounts; JWT bearer tokens from one OpenID Connect provider, roles from a
  configurable claim path, a development Keycloak with an imported realm (compose profile `sso`), and
  an enterprise SSO recipe for brokering customers' SAML or OIDC identity providers.
- Browser sessions: same-site `__Host-` cookie sessions for a Next.js frontend, with antiforgery
  tokens checked on every unsafe request, and logout.
- MFA and passkeys: authenticator-app two-factor with recovery codes, and WebAuthn passkeys for
  registration and sign-in (Identity schema v3), tested with a software authenticator.
- Roles and permissions: `admin` and `auditor` roles mapped to permissions, `RequirePermission`,
  admin endpoints for notes and users, and forbidden-access tests.
- API keys: hashed with SHA-256, scoped, expiring, rotatable with an overlap and revocable. Keys act
  for their owner only on endpoints that opt in, never on key management or admin.
- Profiles: `GET /v1/me` with roles and permissions, `PUT /v1/me/profile` for display name, time
  zone, locale and theme.

### Changed

- Every `--auth` mode turns PostgreSQL on.
- `template-origin.json` records `email`.
- CI runs the ten code-relevant combinations with `--agents` rotated across them.

## [0.1.0] - Unreleased

First release (R01, Core API).

### Added

- `ek-api` template: a hardened ASP.NET Core HTTP API on .NET 10 in a compact single-project layout
  with feature folders, plus unit and integration test projects.
- Base API: typed options validated on start, secrets kept out of the repository, ProblemDetails with
  trace ids, built-in validation, OpenAPI with Scalar, `/v1` routes, paging, per-caller rate limiting,
  CORS and forwarded headers from settings, `TimeProvider`, graceful shutdown, HTTPS redirect, HSTS
  and security headers. Example feature: Notes.
- Observability: JSON logs with trace ids and no secrets, OpenTelemetry traces, metrics and logs over
  OTLP, live and ready health checks.
- `--db postgres`: EF Core with Npgsql, the first migration, unique indexes, development-only seed
  data, Testcontainers, a migration bundle image, a compose service and a nightly `pg_dump` script.
- `--auth identity`: ASP.NET Core Identity API endpoints with bearer tokens, lockout, email
  confirmation and reset with a development sender that logs the link, an owner-only policy on
  Notes, and forbidden-access, mass-assignment and no-secrets-in-logs tests.
- Containers: multi-arch (amd64, arm64) image, Docker Compose, and a Raspberry Pi or VPS recipe with
  HTTPS through a Cloudflare Tunnel. Generated CI, container publishing with signed provenance and
  Dependabot.
- `--agents shared|claude`: `AGENTS.md` written for the chosen options, and a `CLAUDE.md` that imports it.
- `template-origin.json` in every generated project, recording the template version and options.
- Template repository: acceptance test for all nine supported combinations in CI, TemplateVerifier
  snapshot tests, Dependabot, and a release workflow that attests build provenance for the package.
