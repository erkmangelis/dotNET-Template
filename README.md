# Erkmangelis .NET templates

`dotnet new` templates for .NET 10. Release 0.2 has one: **`ek-api`**, a hardened ASP.NET Core HTTP
API that fixes the security, testing and secrets gaps that come up again and again in project reviews.

```bash
dotnet new install Erkmangelis.Templates.0.2.0.nupkg
dotnet new ek-api -n RideSense --auth identity --db postgres --agents claude
```

New here? Follow [docs/getting-started.md](docs/getting-started.md).

## Options

| Option | Values | Default | Adds |
| --- | --- | --- | --- |
| `--auth` | `none`, `identity`, `external`, `oidc` | `none` | `identity`: ASP.NET Core Identity accounts with bearer tokens or same-site cookie sessions with antiforgery, lockout, email confirmation and reset, authenticator-app MFA, passkeys. `external`: the same accounts plus Google, Apple and Microsoft sign-in, linked only on a provider-verified email. `oidc`: no local accounts; the API trusts access tokens from one OpenID Connect provider, with a development Keycloak and an enterprise SSO (SAML) recipe. Every mode adds roles and permissions, admin endpoints, hashed and scoped API keys, user profiles and an owner-only policy on the example feature, and turns PostgreSQL on when `--db` is `none`. |
| `--db` | `none`, `postgres`, `sqlserver`, `sqlite` | `none` | EF Core with migrations, record metadata, soft delete, audit history, a migration bundle, compose and a backup script. `postgres`: PostgreSQL 18 through Npgsql. `sqlserver`: SQL Server 2022 (Express by default; you accept its licence in `.env`; amd64 only). `sqlite`: one file on a volume, no database server. PostgreSQL and SQL Server tests use Testcontainers. A Dapper recipe covers hand-written SQL for hot read paths. |
| `--email` | `none`, `smtp` | `none` | Templated confirmation and reset emails through SMTP (MailKit), Mailpit for local testing. With `none`, emails are only logged, in development. |
| `--agents` | `none`, `shared`, `claude` | `shared` | `AGENTS.md` written for the chosen options; `claude` adds a `CLAUDE.md` that imports it. |

Every `--auth` mode but `none` needs a database, so with `--db none` it generates the PostgreSQL
output. CI runs every auth and email choice on PostgreSQL, and every auth choice on SQL Server and
SQLite (the email option does not touch database code): eighteen combinations that change code.
`--agents` only adds or removes `AGENTS.md` and `CLAUDE.md`, so CI rotates it across them instead of
multiplying them by three, and a snapshot test checks that it touches nothing else.

## Supported means tested

A combination is supported only if CI generates it and it passes the acceptance test
([scripts/acceptance.sh](scripts/acceptance.sh)). For each of the ten combinations, CI:

1. generates the project outside the source tree from the packed `.nupkg`,
2. checks that no template markers and nothing from unselected options are left behind,
3. restores, builds with warnings as errors and runs the format check,
4. runs all generated tests, including ownership, forbidden-access, mass-assignment, API key scope,
   no-secrets-in-logs and (with `--auth external`) unverified-email takeover tests,
5. checks that a deliberate style violation fails the build,
6. starts it with `docker compose` and expects 200 from `/health/ready`, and with `--email smtp` and
   local accounts, that registering sends a confirmation email to the bundled Mailpit.

Snapshot tests ([tests/Erkmangelis.Templates.Tests](tests/Erkmangelis.Templates.Tests)) pin the
generated files for each combination, so every change to the output shows up in review.

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

No default dependency needs a commercial licence: no MediatR, no AutoMapper.

## Working on the template

```bash
dotnet test tests/Erkmangelis.Templates.Tests        # snapshot tests
scripts/acceptance.sh --auth identity --db postgres --email smtp --agents claude --compose
```

Template sources live in [templates/ek-api](templates/ek-api). Conditional code uses the template
engine's `#if (UsePostgres)`-style symbols defined in
[template.json](templates/ek-api/.template.config/template.json). After an intended change to the
output, run the snapshot tests and accept the new output with `scripts/accept-snapshots.sh`, then review the snapshot diff.

Migrations ship pre-generated in `templates/ek-api/.migrations`, one set per sign-in mode. After a
change to the EF model, run `scripts/add-migration.sh <Name>` to add a migration to every set from
the current template; running it again with the newest migration's name replaces that migration.

Releases: push a `v*` tag. The release workflow packs the template, attests its build provenance
and attaches the `.nupkg` to a GitHub release. See [CHANGELOG.md](CHANGELOG.md).

Security baseline: [docs/asvs-l1.md](docs/asvs-l1.md) maps OWASP ASVS Level 1 to what the template
covers and what each project still owns.
