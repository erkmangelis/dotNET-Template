# AGENTS.md

Guidance for AI coding agents working in this repository. Humans: see README.md.

## Project

Acme.Notes is an ASP.NET Core HTTP API on .NET 10 (minimal APIs), generated from the `ek-api`
template with these options:
- Authentication: none. Every endpoint is anonymous.
- Database: none. Notes are kept in memory by `InMemoryNoteStore`.
- Email: SMTP through MailKit, or the log in Development (`src/Acme.Notes.Api/Infrastructure/Email`).

## Commands

Run these before you say a change is done. All must pass.

```bash
dotnet build -warnaserror
dotnet format --verify-no-changes
dotnet test
```


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
- Send email by rendering a message with `EmailTemplates` (add a method for a new message) and
  passing it to `IEmailTransport`. Never build HTML by hand from user input.
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
