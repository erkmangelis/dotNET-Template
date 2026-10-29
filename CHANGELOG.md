# Changelog

All notable changes to the template package. Versions follow [Semantic Versioning](https://semver.org/).

## [0.3.0] - Unreleased

R03, Data and API depth.

### Added

- Filtering and sorting on `GET /v1/notes` (and the admin list): `search` matches titles ignoring
  case with LIKE wildcards taken literally, `createdFrom` and `createdBefore` bound the creation
  time, and `sort` takes up to three allowlisted fields (`createdAt`, `title`), each with an optional
  `-` for descending. Anything else answers 400. `SortFields<T>` is the reusable allowlist.

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
