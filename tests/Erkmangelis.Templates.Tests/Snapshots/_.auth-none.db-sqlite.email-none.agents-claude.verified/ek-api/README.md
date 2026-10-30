# Acme.Notes

An HTTP API on .NET 10, generated from the `ek-api` template (see `template-origin.json`).

## Run it

```bash
dotnet run --project src/Acme.Notes.Api
```

Open <http://localhost:5080/scalar> for the API reference. Health: `/health/live` and `/health/ready`.
[docs/api.md](docs/api.md) covers paging, filtering, concurrent edits and safe retries, and
[docs/background-work.md](docs/background-work.md) covers work that runs outside requests.

## Test it

```bash
dotnet build
dotnet format --verify-no-changes
dotnet test
```

Integration tests use a throwaway SQLite file, so they need no Docker.
CI (`.github/workflows/ci.yml`) runs the same commands with warnings as errors.

## Layout

```text
src/Acme.Notes.Api/
  Program.cs              composition root
  Infrastructure/         options, ProblemDetails, OpenAPI, rate limits, CORS, security headers, telemetry, health
  Infrastructure/Idempotency/  Idempotency-Key handling for retried writes
  Infrastructure/Jobs/    background work: the scoped background loop
  Common/                 shared types: paging, sorting, record metadata, soft delete
  Features/Notes/         the example feature: contracts, endpoints, storage
  Data/                   AppDbContext, entity configuration, audit interceptor, migrations, development seed
  Features/Audit/         audit history endpoints
tests/Acme.Notes.Api.UnitTests/
tests/Acme.Notes.Api.IntegrationTests/
```

Add a feature as a new folder under `Features/` with its own endpoints, contracts and storage, and
register it in `Program.cs` the way Notes is.

## Ship it

`docker compose up -d --build` runs the production image locally. [docs/deployment.md](docs/deployment.md)
covers the Raspberry Pi or VPS setup with HTTPS through a Cloudflare Tunnel.
[docs/database.md](docs/database.md) covers migrations, the migration bundle and backups, and
[docs/dapper.md](docs/dapper.md) hand-written SQL for a hot read path.

## Secrets

Nothing secret lives in the repository. Development uses user secrets; containers read environment
variables from `.env`, which git ignores. The app refuses to start when a required setting is
missing or invalid.
