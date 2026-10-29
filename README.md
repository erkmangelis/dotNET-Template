# Erkmangelis .NET templates

`dotnet new` templates for .NET 10. Release 0.1 has one: **`ek-api`**, a hardened ASP.NET Core HTTP
API that fixes the security, testing and secrets gaps that come up again and again in project reviews.

```bash
dotnet new install Erkmangelis.Templates.0.1.0.nupkg
dotnet new ek-api -n RideSense --auth identity --db postgres --agents claude
```

New here? Follow [docs/getting-started.md](docs/getting-started.md).

## Options

| Option | Values | Default | Adds |
| --- | --- | --- | --- |
| `--auth` | `none`, `identity` | `none` | ASP.NET Core Identity API endpoints with bearer tokens, lockout, email confirmation and reset, an owner-only policy on the example feature. Turns PostgreSQL on. |
| `--db` | `none`, `postgres` | `none` | PostgreSQL 18 through EF Core and Npgsql, the first migration, Testcontainers, a migration bundle, a compose service and a backup script. |
| `--agents` | `none`, `shared`, `claude` | `shared` | `AGENTS.md` written for the chosen options; `claude` adds a `CLAUDE.md` that imports it. |

`--auth identity` needs a database, so it always generates the PostgreSQL output, with or without
`--db postgres`. That leaves three supported auth and database combinations, each with three agent
choices.

## Supported means tested

A combination is supported only if CI generates it and it passes the acceptance test
([scripts/acceptance.sh](scripts/acceptance.sh)). For each of the nine combinations, CI:

1. generates the project outside the source tree from the packed `.nupkg`,
2. checks that no template markers and nothing from unselected options are left behind,
3. restores, builds with warnings as errors and runs the format check,
4. runs all generated tests, including the ownership, mass-assignment and no-secrets-in-logs tests with `--auth identity`,
5. checks that a deliberate style violation fails the build,
6. starts it with `docker compose` and expects 200 from `/health/ready`.

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
scripts/acceptance.sh --auth identity --db postgres --agents claude --compose
```

Template sources live in [templates/ek-api](templates/ek-api). Conditional code uses the template
engine's `#if (UsePostgres)`-style symbols defined in
[template.json](templates/ek-api/.template.config/template.json). After an intended change to the
output, update the snapshots: `EK_ACCEPT_SNAPSHOTS=1 dotnet test tests/Erkmangelis.Templates.Tests`.

Releases: push a `v*` tag. The release workflow packs the template, attests its build provenance
and attaches the `.nupkg` to a GitHub release. See [CHANGELOG.md](CHANGELOG.md).

Security baseline: [docs/asvs-l1.md](docs/asvs-l1.md) maps OWASP ASVS Level 1 to what the template
covers and what each project still owns.
