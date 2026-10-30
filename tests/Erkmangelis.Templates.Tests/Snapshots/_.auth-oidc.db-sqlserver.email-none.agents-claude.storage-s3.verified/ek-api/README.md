# Acme.Notes

An HTTP API on .NET 10, generated from the `ek-api` template (see `template-origin.json`).

## Run it

```bash
cp .env.example .env                 # set MSSQL_ACCEPT_EULA=Y and MSSQL_SA_PASSWORD
docker compose up -d db
dotnet user-secrets set ConnectionStrings:Default "Server=localhost;Database=app;User Id=sa;Password=<the same password>;TrustServerCertificate=True" --project src/Acme.Notes.Api
dotnet run --project src/Acme.Notes.Api
```

Open <http://localhost:5080/scalar> for the API reference. Health: `/health/live` and `/health/ready`.
[docs/api.md](docs/api.md) covers paging, filtering, concurrent edits and safe retries, and
[docs/background-work.md](docs/background-work.md) covers work that runs outside requests.
[docs/files.md](docs/files.md) covers uploads, downloads, signed links and file cleanup; run
`docker compose up -d storage` and set the storage keys in user secrets first.

### Sign in

Accounts live in your OpenID Connect provider (Keycloak, Entra ID, Auth0 and so on). Clients sign in
there and call the API with the provider's access token: `Authorization: Bearer <accessToken>`.
Set `Oidc:Authority` (the issuer) and `Oidc:Audience` (the `aud` the provider gives this API), and
`Oidc:RoleClaim` if roles are not in a `roles` claim. Development points at a Keycloak realm on
<http://localhost:8081>; start it with `docker compose --profile sso up -d keycloak` and get a token
for the seeded user:

```bash
curl -s -d grant_type=password -d client_id=acme-notes-web -d username=dev@example.com \
  -d password=dev-password http://localhost:8081/realms/acme-notes/protocol/openid-connect/token
```

Customers who sign in with their company's SAML or OIDC identity provider go through the same
provider: see [docs/enterprise-sso.md](docs/enterprise-sso.md).

### Roles and permissions

Endpoints check permissions; roles grant them. `src/Acme.Notes.Api/Features/Auth/Permissions.cs`
lists both: `admin` can do everything, `auditor` can read everything. Admin endpoints live under
`/v1/admin`, for example `GET /v1/admin/notes`.
Roles come from the provider's tokens (`Oidc:RoleClaim`). Create realm or app roles named `admin`
and `auditor` there and assign them to users.

### API keys

Scripts and other services call the API with a key instead of a user session. A signed-in user
creates one, and the response shows the key once:

```http
POST /v1/api-keys
{ "name": "Nightly export", "scopes": ["notes:read"], "expiresInDays": 90 }
```

Send it as the `X-Api-Key` header. A key acts as its owner but only within its scopes
(`notes:read`, `notes:write`), and it never reaches `/v1/api-keys` or `/v1/admin`. Only a SHA-256
hash is stored. `POST /v1/api-keys/{id}/rotate` issues a replacement and keeps the old key working
for `overlapHours` (default 0), and `DELETE /v1/api-keys/{id}` revokes it.

### Profile and preferences

`GET /v1/me` returns the signed-in user's id, email, roles, permissions and profile, so a frontend
can shape its UI in one call. `PUT /v1/me/profile` replaces the profile: `displayName`, `timeZone`
(IANA, such as `Europe/Istanbul`), `locale` (such as `tr-TR`) and `theme` (`system`, `light` or
`dark`). Users without a saved profile get UTC, `en` and `system`.

## Test it

```bash
dotnet build
dotnet format --verify-no-changes
dotnet test
```

Integration tests start throwaway SQL Server and RustFS containers with Testcontainers, so Docker must be running.
CI (`.github/workflows/ci.yml`) runs the same commands with warnings as errors.

## Layout

```text
src/Acme.Notes.Api/
  Program.cs              composition root
  Infrastructure/         options, ProblemDetails, OpenAPI, rate limits, CORS, security headers, telemetry, health
  Infrastructure/Idempotency/  Idempotency-Key handling for retried writes
  Infrastructure/Jobs/    background work: the scoped background loop
  Infrastructure/Storage/ private S3-compatible object storage and signed links
  Common/                 shared types: paging, sorting, record metadata, soft delete
  Features/Notes/         the example feature: contracts, endpoints, storage
  Features/Files/         uploads (API, signed links, tus), checks, thumbnails, downloads, cleanup
  Features/Auth/          access-token validation for the OpenID Connect provider
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

## Update from the template

`template-origin.json` records the template version and options this project was generated with.
With the [`ek` tool](https://github.com/erkmangelis/dotNET-Template/blob/main/docs/cli.md), run
`ek update` on a clean git working tree to take a newer template version's changes: it merges them
into your files, marks conflicts for review and never overwrites what you changed.
