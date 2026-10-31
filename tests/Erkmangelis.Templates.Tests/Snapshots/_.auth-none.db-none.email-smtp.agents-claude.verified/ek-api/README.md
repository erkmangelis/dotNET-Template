# Acme.Notes

An HTTP API on .NET 10, generated from the `ek-api` template (see `template-origin.json`).

## Run it

```bash
dotnet run --project src/Acme.Notes.Api
```

Open <http://localhost:5080/scalar> for the API reference. Health: `/health/live` and `/health/ready`.
[docs/api.md](docs/api.md) covers paging, filtering, concurrent edits and safe retries, and
[docs/background-work.md](docs/background-work.md) covers work that runs outside requests.
[docs/outbound-http.md](docs/outbound-http.md) covers calling other services safely.

### Email

In Development, email is written to the console log (`Email:Transport` is `Log`). To see real
messages, start Mailpit and point the app at it, then open <http://localhost:8025>:

```bash
docker compose --profile mail up -d mailpit
dotnet user-secrets set Email:Transport Smtp --project src/Acme.Notes.Api
dotnet user-secrets set Email:Smtp:Host localhost --project src/Acme.Notes.Api
dotnet user-secrets set Email:Smtp:Port 1025 --project src/Acme.Notes.Api
dotnet user-secrets set Email:Smtp:Security None --project src/Acme.Notes.Api
```

In production, set `EMAIL_FROM` and the `SMTP_*` values in `.env`. The messages themselves live in
`src/Acme.Notes.Api/Infrastructure/Email/EmailTemplates.cs`.

## Test it

```bash
dotnet build
dotnet format --verify-no-changes
dotnet test
```

CI (`.github/workflows/ci.yml`) runs the same commands with warnings as errors.

## Layout

```text
src/Acme.Notes.Api/
  Program.cs              composition root
  Infrastructure/         options, ProblemDetails, OpenAPI, rate limits, CORS, security headers, telemetry, health
  Infrastructure/Idempotency/  Idempotency-Key handling for retried writes
  Infrastructure/Http/    clients for other services: timeouts, retries, allowed hosts
  Infrastructure/Jobs/    background work: the scoped background loop
  Infrastructure/Email/   email transports (SMTP, development log) and message templates
  Common/                 shared types: paging, sorting, record metadata, soft delete
  Features/Notes/         the example feature: contracts, endpoints, storage
tests/Acme.Notes.Api.UnitTests/
tests/Acme.Notes.Api.IntegrationTests/
```

Add a feature as a new folder under `Features/` with its own endpoints, contracts and storage, and
register it in `Program.cs` the way Notes is.

## Ship it

`docker compose up -d --build` runs the production image locally. [docs/deployment.md](docs/deployment.md)
covers the Raspberry Pi or VPS setup with HTTPS through a Cloudflare Tunnel.

## Secrets

Nothing secret lives in the repository. Development uses user secrets; containers read environment
variables from `.env`, which git ignores. The app refuses to start when a required setting is
missing or invalid.

## Update from the template

`template-origin.json` records the template version and options this project was generated with.
With the [`ek` tool](https://github.com/erkmangelis/dotNET-Template/blob/main/docs/cli.md), run
`ek update` on a clean git working tree to take a newer template version's changes: it merges them
into your files, marks conflicts for review and never overwrites what you changed.
