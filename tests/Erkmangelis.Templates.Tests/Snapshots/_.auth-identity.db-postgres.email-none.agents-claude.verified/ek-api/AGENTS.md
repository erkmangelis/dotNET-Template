# AGENTS.md

Guidance for AI coding agents working in this repository. Humans: see README.md.

## Project

Acme.Notes is an ASP.NET Core HTTP API on .NET 10 (minimal APIs), generated from the `ek-api`
template with these options:
- Authentication: ASP.NET Core Identity API endpoints with bearer tokens under `/v1/auth`.
- Database: PostgreSQL 18 through EF Core and Npgsql (`src/Acme.Notes.Api/Data`).

## Commands

Run these before you say a change is done. All must pass.

```bash
dotnet build -warnaserror
dotnet format --verify-no-changes
dotnet test
```

Integration tests need Docker (Testcontainers starts PostgreSQL).

After changing an entity or its configuration, add a migration:
`dotnet tool restore && dotnet ef migrations add <Name> --project src/Acme.Notes.Api --output-dir Data/Migrations`.
Never edit a migration that has been merged.

## Layout and conventions

- One project, feature folders: `src/Acme.Notes.Api/Features/<Feature>/` holds that feature's
  contracts, endpoints and storage. Cross-cutting setup lives in `Infrastructure/`.
- Routes are versioned: map new endpoints on the `/v1` group in `Program.cs`.
- Endpoints return `TypedResults`. Errors are ProblemDetails (`TypedResults.Problem`), never plain strings.
- Validate request contracts with DataAnnotations; the built-in validation runs them.
- Request contracts list only the fields a client may set. Never bind an entity directly.
- Settings are typed options classes with `ValidateOnStart`. No secrets in appsettings files.
- Use the injected `TimeProvider`, not `DateTime.Now`, so tests can control time.
- Never set `CreatedAt`, `UpdatedAt` or `DeletedAt` (or the `*By` fields) by hand, and never hard-delete
  a soft-deletable entity: `AuditableEntityInterceptor` does both on save. See `docs/database.md`.
- Log with `[LoggerMessage]` source-generated methods. Never log passwords, tokens, links with
  codes, connection strings or request bodies.
- The project's own namespaces are imported in `GlobalUsings.cs`, not in each file.
- Naming: PascalCase types and members, `I`-prefixed interfaces, camelCase locals and parameters,
  `_camelCase` private fields. No Async suffix rule.
- Packages: add versions to `Directory.Packages.props` only. Do not add packages that need a
  commercial licence (for example MediatR or AutoMapper).

## Security rules

- Every endpoint that touches user data requires authorization.
- Cookie sessions are checked for an antiforgery token on every unsafe request
  (`BrowserSessions.UseBrowserSessionAntiforgery`). Do not add endpoints that bypass it.
- Guard admin endpoints with `.RequirePermission(Permissions.X)`, never with role names. Roles map
  to permissions in `Features/Auth/Permissions.cs`; add a permission there for a new admin action.
- Endpoints an API key may call use a user-or-key policy (`services.AddUserOrKeyPolicy(name, scope)`,
  see `AddNotes`) with a scope from `ApiKeyScopes`. Everything else stays user-only, so keys never
  manage keys or reach admin endpoints.
- Check ownership with the resource policy (`NotePolicies.Owner` is the example) and answer 404,
  not 403, for another user's resource.
- Add an integration test proving one user cannot read or change another user's data.

## Tests

- Unit tests: `tests/Acme.Notes.Api.UnitTests`, for logic without HTTP.
- Integration tests: `tests/Acme.Notes.Api.IntegrationTests`, through `ApiFactory`, one running API
  per test run. Make test data unique (`Guid.NewGuid()`), because tests share it.
