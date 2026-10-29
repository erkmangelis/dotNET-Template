# Acme.Notes

An HTTP API on .NET 10, generated from the `ek-api` template (see `template-origin.json`).

## Run it

```bash
cp .env.example .env                 # set POSTGRES_PASSWORD
docker compose up -d db
dotnet user-secrets set ConnectionStrings:Default "Host=localhost;Database=app;Username=app;Password=<the same password>" --project src/Acme.Notes.Api
dotnet run --project src/Acme.Notes.Api
```

Open <http://localhost:5080/scalar> for the API reference. Health: `/health/live` and `/health/ready`.

### Sign in

1. `POST /v1/auth/register` with `{ "email": "...", "password": "..." }` (12 characters or more).
2. In Development the confirmation link is written to the console log. Open it.
3. `POST /v1/auth/login` returns an access token and a refresh token. Send `Authorization: Bearer <accessToken>`.
4. `POST /v1/auth/refresh` with the refresh token when the access token expires (after 60 minutes).

To get a confirmed `dev@example.com` user with sample notes, set a password in user secrets before
the first run: `dotnet user-secrets set DevelopmentSeed:UserPassword "<password>" --project src/Acme.Notes.Api`.

No email provider is set up yet, so outside Development confirmation emails are dropped (and logged
as a warning). Until you add one, either confirm users in the database or set
`Identity__SignIn__RequireConfirmedEmail=false`.

## Test it

```bash
dotnet build
dotnet format --verify-no-changes
dotnet test
```

Integration tests start a throwaway PostgreSQL container with Testcontainers, so Docker must be running.
CI (`.github/workflows/ci.yml`) runs the same commands with warnings as errors.

## Layout

```text
src/Acme.Notes.Api/
  Program.cs              composition root
  Infrastructure/         options, ProblemDetails, OpenAPI, rate limits, CORS, security headers, telemetry, health
  Common/                 shared types such as paging
  Features/Notes/         the example feature: contracts, endpoints, storage
  Features/Auth/          ASP.NET Core Identity setup and email senders
  Data/                   AppDbContext, entity configuration, migrations, development seed
tests/Acme.Notes.Api.UnitTests/
tests/Acme.Notes.Api.IntegrationTests/
```

Add a feature as a new folder under `Features/` with its own endpoints, contracts and storage, and
register it in `Program.cs` the way Notes is.

## Ship it

`docker compose up -d --build` runs the production image locally. [docs/deployment.md](docs/deployment.md)
covers the Raspberry Pi or VPS setup with HTTPS through a Cloudflare Tunnel.
[docs/database.md](docs/database.md) covers migrations, the migration bundle and backups.

## Secrets

Nothing secret lives in the repository. Development uses user secrets; containers read environment
variables from `.env`, which git ignores. The app refuses to start when a required setting is
missing or invalid.
