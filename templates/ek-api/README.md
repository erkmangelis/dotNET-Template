# MyProject

An HTTP API on .NET 10, generated from the `ek-api` template (see `template-origin.json`).

## Run it

```bash
<!--#if (UsePostgres) -->
cp .env.example .env                 # set POSTGRES_PASSWORD
docker compose up -d db
dotnet user-secrets set ConnectionStrings:Default "Host=localhost;Database=app;Username=app;Password=<the same password>" --project src/MyProject.Api
<!--#endif -->
dotnet run --project src/MyProject.Api
```

Open <http://localhost:5080/scalar> for the API reference. Health: `/health/live` and `/health/ready`.
<!--#if (UseOidc) -->

### Sign in

Accounts live in your OpenID Connect provider (Keycloak, Entra ID, Auth0 and so on). Clients sign in
there and call the API with the provider's access token: `Authorization: Bearer <accessToken>`.
Set `Oidc:Authority` (the issuer) and `Oidc:Audience` (the `aud` the provider gives this API), and
`Oidc:RoleClaim` if roles are not in a `roles` claim. Development points at a Keycloak realm on
<http://localhost:8081>; start it with `docker compose --profile sso up -d keycloak` and get a token
for the seeded user:

```bash
curl -s -d grant_type=password -d client_id=ek-app-slug-web -d username=dev@example.com \
  -d password=dev-password http://localhost:8081/realms/ek-app-slug/protocol/openid-connect/token
```

Customers who sign in with their company's SAML or OIDC identity provider go through the same
provider: see [docs/enterprise-sso.md](docs/enterprise-sso.md).
<!--#endif -->
<!--#if (UseIdentity) -->

### Sign in

1. `POST /v1/auth/register` with `{ "email": "...", "password": "..." }` (12 characters or more).
2. In Development the confirmation link is written to the console log. Open it.
3. `POST /v1/auth/login` returns an access token and a refresh token. Send `Authorization: Bearer <accessToken>`.
4. `POST /v1/auth/refresh` with the refresh token when the access token expires (after 60 minutes).

Two-factor sign-in with an authenticator app is built in: `POST /v1/auth/manage/2fa` returns a
shared key for the app, and the same endpoint with `{ "enable": true, "twoFactorCode": "..." }`
turns it on and returns recovery codes. After that, `/login` needs `twoFactorCode` or
`twoFactorRecoveryCode` as well.

Passkeys: a signed-in user calls `POST /v1/auth/passkeys/creation-options`, passes the result to
`navigator.credentials.create()`, and posts the credential to `POST /v1/auth/passkeys`. To sign in,
`POST /v1/auth/passkeys/request-options`, `navigator.credentials.get()`, then
`POST /v1/auth/passkeys/login`. Send cookies on both calls of each pair. If the frontend and the API
are on different hosts of one site, set `Passkeys:ServerDomain` to the site (for example `example.com`).

Browser frontends on the same site (for example a Next.js app on `app.example.com` calling
`api.example.com`) should use a cookie session instead of tokens:

1. `POST /v1/auth/login?useCookies=true`. The session cookie is HttpOnly, Secure, SameSite=Strict.
2. `GET /v1/auth/antiforgery`, then send its `token` in the `X-CSRF-TOKEN` header on every
   POST, PUT, PATCH and DELETE. Requests made with the cookie and without the token get 400.
3. `POST /v1/auth/logout` ends the session.

If the frontend calls the API from another origin, list it in `Cors:AllowedOrigins` and set
`Cors:AllowCredentials` to `true`. A frontend that proxies the API under its own origin needs neither.
<!--#if (UseExternal) -->

To sign in with Google, Apple or Microsoft, the app uses the provider's own SDK (Google Identity
Services, Sign in with Apple, MSAL), then sends the ID token: `POST /v1/auth/external/google` with
`{ "idToken": "..." }`. The answer is the same as `/v1/auth/login`. Turn a provider on by setting
its client id, for example `ExternalAuth:Providers:Google:ClientIds:0`. A provider's login joins an
existing account only when the provider says the email is verified; a signed-in user can link any
login with `POST /v1/auth/external/{provider}/link`.
<!--#endif -->

To get a confirmed `dev@example.com` user with sample notes, set a password in user secrets before
the first run: `dotnet user-secrets set DevelopmentSeed:UserPassword "<password>" --project src/MyProject.Api`.

<!--#if (!UseSmtp) -->
No email provider is set up (the template's `--email smtp` option adds one), so outside Development
confirmation emails are dropped and logged as a warning. Until you add one, either confirm users in
the database or set `Identity__SignIn__RequireConfirmedEmail=false`.
<!--#endif -->
<!--#endif -->
<!--#if (UseSmtp) -->

### Email

In Development, email is written to the console log (`Email:Transport` is `Log`). To see real
messages, start Mailpit and point the app at it, then open <http://localhost:8025>:

```bash
docker compose --profile mail up -d mailpit
dotnet user-secrets set Email:Transport Smtp --project src/MyProject.Api
dotnet user-secrets set Email:Smtp:Host localhost --project src/MyProject.Api
dotnet user-secrets set Email:Smtp:Port 1025 --project src/MyProject.Api
dotnet user-secrets set Email:Smtp:Security None --project src/MyProject.Api
```

In production, set `EMAIL_FROM` and the `SMTP_*` values in `.env`. The messages themselves live in
`src/MyProject.Api/Infrastructure/Email/EmailTemplates.cs`.
<!--#endif -->

<!--#if (UseAuth) -->
### Roles and permissions

Endpoints check permissions; roles grant them. `src/MyProject.Api/Features/Auth/Permissions.cs`
lists both: `admin` can do everything, `auditor` can read everything. Admin endpoints live under
`/v1/admin`, for example `GET /v1/admin/notes`.
<!--#if (UseIdentity) -->
The roles are seeded by the migrations. Admins change a user's roles with
`PUT /v1/admin/users/{id}/roles`; the change applies from the user's next sign-in. To make the first
admin, add the role in the database:

```sql
INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id" FROM "AspNetUsers" u, "AspNetRoles" r
WHERE u."NormalizedEmail" = upper('you@example.com') AND r."Name" = 'admin';
```

The development seed user `dev@example.com` is an admin.
<!--#else -->
Roles come from the provider's tokens (`Oidc:RoleClaim`). Create realm or app roles named `admin`
and `auditor` there and assign them to users.
<!--#endif -->

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

<!--#endif -->
## Test it

```bash
dotnet build
dotnet format --verify-no-changes
dotnet test
```

<!--#if (UsePostgres) -->
Integration tests start a throwaway PostgreSQL container with Testcontainers, so Docker must be running.
<!--#endif -->
CI (`.github/workflows/ci.yml`) runs the same commands with warnings as errors.

## Layout

```text
src/MyProject.Api/
  Program.cs              composition root
  Infrastructure/         options, ProblemDetails, OpenAPI, rate limits, CORS, security headers, telemetry, health
<!--#if (UseSmtp) -->
  Infrastructure/Email/   email transports (SMTP, development log) and message templates
<!--#endif -->
  Common/                 shared types such as paging
  Features/Notes/         the example feature: contracts, endpoints, storage
<!--#if (UseExternal) -->
  Features/Auth/          ASP.NET Core Identity setup, email senders, Google/Apple/Microsoft sign-in
<!--#elif (UseIdentity) -->
  Features/Auth/          ASP.NET Core Identity setup and email senders
<!--#elif (UseOidc) -->
  Features/Auth/          access-token validation for the OpenID Connect provider
<!--#endif -->
<!--#if (UsePostgres) -->
  Data/                   AppDbContext, entity configuration, migrations, development seed
<!--#endif -->
tests/MyProject.Api.UnitTests/
tests/MyProject.Api.IntegrationTests/
```

Add a feature as a new folder under `Features/` with its own endpoints, contracts and storage, and
register it in `Program.cs` the way Notes is.

## Ship it

`docker compose up -d --build` runs the production image locally. [docs/deployment.md](docs/deployment.md)
covers the Raspberry Pi or VPS setup with HTTPS through a Cloudflare Tunnel.
<!--#if (UsePostgres) -->
[docs/database.md](docs/database.md) covers migrations, the migration bundle and backups.
<!--#endif -->

## Secrets

Nothing secret lives in the repository. Development uses user secrets; containers read environment
variables from `.env`, which git ignores. The app refuses to start when a required setting is
missing or invalid.
