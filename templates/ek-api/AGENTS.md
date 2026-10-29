# AGENTS.md

Guidance for AI coding agents working in this repository. Humans: see README.md.

## Project

MyProject is an ASP.NET Core HTTP API on .NET 10 (minimal APIs), generated from the `ek-api`
template with these options:
<!--#if (UseIdentity) -->
- Authentication: ASP.NET Core Identity API endpoints with bearer tokens under `/v1/auth`.
<!--#else -->
- Authentication: none. Every endpoint is anonymous.
<!--#endif -->
<!--#if (UsePostgres) -->
- Database: PostgreSQL 18 through EF Core and Npgsql (`src/MyProject.Api/Data`).
<!--#else -->
- Database: none. Notes are kept in memory by `InMemoryNoteStore`.
<!--#endif -->
<!--#if (UseSmtp) -->
- Email: SMTP through MailKit, or the log in Development (`src/MyProject.Api/Infrastructure/Email`).
<!--#endif -->

## Commands

Run these before you say a change is done. All must pass.

```bash
dotnet build -warnaserror
dotnet format --verify-no-changes
dotnet test
```

<!--#if (UsePostgres) -->
Integration tests need Docker (Testcontainers starts PostgreSQL).

After changing an entity or its configuration, add a migration:
`dotnet tool restore && dotnet ef migrations add <Name> --project src/MyProject.Api --output-dir Data/Migrations`.
Never edit a migration that has been merged.
<!--#endif -->

## Layout and conventions

- One project, feature folders: `src/MyProject.Api/Features/<Feature>/` holds that feature's
  contracts, endpoints and storage. Cross-cutting setup lives in `Infrastructure/`.
- Routes are versioned: map new endpoints on the `/v1` group in `Program.cs`.
- Endpoints return `TypedResults`. Errors are ProblemDetails (`TypedResults.Problem`), never plain strings.
- Validate request contracts with DataAnnotations; the built-in validation runs them.
- Request contracts list only the fields a client may set. Never bind an entity directly.
- Settings are typed options classes with `ValidateOnStart`. No secrets in appsettings files.
- Use the injected `TimeProvider`, not `DateTime.Now`, so tests can control time.
<!--#if (UseSmtp) -->
- Send email by rendering a message with `EmailTemplates` (add a method for a new message) and
  passing it to `IEmailTransport`. Never build HTML by hand from user input.
<!--#endif -->
- Log with `[LoggerMessage]` source-generated methods. Never log passwords, tokens, links with
  codes, connection strings or request bodies.
- The project's own namespaces are imported in `GlobalUsings.cs`, not in each file.
- Naming: PascalCase types and members, `I`-prefixed interfaces, camelCase locals and parameters,
  `_camelCase` private fields. No Async suffix rule.
- Packages: add versions to `Directory.Packages.props` only. Do not add packages that need a
  commercial licence (for example MediatR or AutoMapper).
<!--#if (UseIdentity) -->

## Security rules

- Every endpoint that touches user data requires authorization.
- Check ownership with the resource policy (`NotePolicies.Owner` is the example) and answer 404,
  not 403, for another user's resource.
- Add an integration test proving one user cannot read or change another user's data.
<!--#endif -->

## Tests

- Unit tests: `tests/MyProject.Api.UnitTests`, for logic without HTTP.
- Integration tests: `tests/MyProject.Api.IntegrationTests`, through `ApiFactory`, one running API
  per test run. Make test data unique (`Guid.NewGuid()`), because tests share it.
