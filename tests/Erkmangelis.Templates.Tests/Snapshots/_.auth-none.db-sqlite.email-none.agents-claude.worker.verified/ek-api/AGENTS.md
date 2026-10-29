# AGENTS.md

Guidance for AI coding agents working in this repository. Humans: see README.md.

## Project

Acme.Notes is an ASP.NET Core HTTP API on .NET 10 (minimal APIs), generated from the `ek-api`
template with these options:
- Authentication: none. Every endpoint is anonymous.
- Database: SQLite through EF Core and Microsoft.Data.Sqlite (`src/Acme.Notes.Api/Data`).
- Jobs: Hangfire, stored in the database (`src/Acme.Notes.Api/Infrastructure/Jobs`).
- Worker: `src/Acme.Notes.Worker` runs the jobs; the API only queues them (`Jobs:RunServer` is off).
  The worker references the API project and registers the same services, so a job's dependencies
  must be registered in both `Program.cs` files.

## Commands

Run these before you say a change is done. All must pass.

```bash
dotnet build -warnaserror
dotnet format --verify-no-changes
dotnet test
```

Integration tests run against a temporary SQLite file.

After changing an entity or its configuration, add a migration:
`dotnet tool restore && dotnet ef migrations add <Name> --project src/Acme.Notes.Api --output-dir Data/Migrations`.
Never edit a migration that has been merged.

## Layout and conventions

- One project, feature folders: `src/Acme.Notes.Api/Features/<Feature>/` holds that feature's
  contracts, endpoints and storage. Cross-cutting setup lives in `Infrastructure/`.
- Routes are versioned with Asp.Versioning (`Infrastructure/ApiVersions.cs`): map new endpoints on a
  versioned group in `Program.cs`. Add a version only for a breaking change, and map only the
  endpoints that change to it with `MapToApiVersion`; see `docs/api.md`.
- Endpoints return `TypedResults`. Errors are ProblemDetails (`TypedResults.Problem`), never plain strings.
- Validate request contracts with DataAnnotations; the built-in validation runs them.
- Request contracts list only the fields a client may set. Never bind an entity directly.
- Settings are typed options classes with `ValidateOnStart`. No secrets in appsettings files.
- Use the injected `TimeProvider`, not `DateTime.Now`, so tests can control time.
- Repeating in-process work derives from `ScopedBackgroundLoop`; never start a raw `Task.Run` or
  timer. See `docs/background-work.md`.
- Slow work a request asks for goes into a job (`IJob<TArgs>`, queued with `IJobQueue`); scheduled
  work is an `IRecurringJob`. Job arguments are records of ids and values with arrays, never
  entities or services. Every job must be safe to run twice: wrap effects that must happen once
  in `JobIdempotency.RunOnceAsync` with a key for that piece of work.
- Work that only one instance may do at a time takes an `IDistributedLock` and skips when it is held.
- Never set `CreatedAt`, `UpdatedAt` or `DeletedAt` (or the `*By` fields) by hand, and never hard-delete
  a soft-deletable entity: `AuditableEntityInterceptor` does both on save. See `docs/database.md`.
- Queries go through EF Core. Hand-written SQL (Dapper) is only for a measured hot read path; follow
  `docs/dapper.md`, which keeps the soft-delete filter and parameters.
- Log with `[LoggerMessage]` source-generated methods. Never log passwords, tokens, links with
  codes, connection strings or request bodies.
- The project's own namespaces are imported in `GlobalUsings.cs`, not in each file.
- Naming: PascalCase types and members, `I`-prefixed interfaces, camelCase locals and parameters,
  `_camelCase` private fields. No Async suffix rule.
- Packages: add versions to `Directory.Packages.props` only. Do not add packages that need a
  commercial licence (for example MediatR or AutoMapper).

## Tests

- Unit tests: `tests/Acme.Notes.Api.UnitTests`, for logic without HTTP.
- Integration tests: `tests/Acme.Notes.Api.IntegrationTests`, through `ApiFactory`, one running API
  per test run. Make test data unique (`Guid.NewGuid()`), because tests share it.
