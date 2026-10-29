# Changelog

All notable changes to the template package. Versions follow [Semantic Versioning](https://semver.org/).

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
