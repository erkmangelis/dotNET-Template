# AGENTS.md

Guidance for AI coding agents working in this repository. Humans: see README.md.

## Project

MyProject is an ASP.NET Core HTTP API on .NET 10 (minimal APIs), generated from the `ek-api`
template with these options:
<!--#if (UseExternal) -->
- Authentication: ASP.NET Core Identity API endpoints under `/v1/auth`, plus sign-in with Google,
  Apple and Microsoft ID tokens under `/v1/auth/external` (`Features/Auth/External*.cs`).
<!--#elif (UseIdentity) -->
- Authentication: ASP.NET Core Identity API endpoints with bearer tokens under `/v1/auth`.
<!--#elif (UseOidc) -->
- Authentication: a hosted OpenID Connect provider owns the accounts. The API only validates its
  access tokens (`Features/Auth/OidcSetup.cs`); the user id is the token's `sub`.
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
- Routes are versioned with Asp.Versioning (`Infrastructure/ApiVersions.cs`): map new endpoints on a
  versioned group in `Program.cs`. Add a version only for a breaking change, and map only the
  endpoints that change to it with `MapToApiVersion`; see `docs/api.md`.
- Endpoints return `TypedResults`. Errors are ProblemDetails (`TypedResults.Problem`), never plain strings.
- Validate request contracts with DataAnnotations; the built-in validation runs them.
- Request contracts list only the fields a client may set. Never bind an entity directly.
- Settings are typed options classes with `ValidateOnStart`. No secrets in appsettings files.
- Use the injected `TimeProvider`, not `DateTime.Now`, so tests can control time.
<!--#if (UsePostgres) -->
- Never set `CreatedAt`, `UpdatedAt` or `DeletedAt` (or the `*By` fields) by hand, and never hard-delete
  a soft-deletable entity: `AuditableEntityInterceptor` does both on save. See `docs/database.md`.
<!--#endif -->
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
<!--#if (UseAuth) -->

## Security rules

- Every endpoint that touches user data requires authorization.
<!--#if (UseIdentity) -->
- Cookie sessions are checked for an antiforgery token on every unsafe request
  (`BrowserSessions.UseBrowserSessionAntiforgery`). Do not add endpoints that bypass it.
<!--#endif -->
- Guard admin endpoints with `.RequirePermission(Permissions.X)`, never with role names. Roles map
  to permissions in `Features/Auth/Permissions.cs`; add a permission there for a new admin action.
- Endpoints an API key may call use a user-or-key policy (`services.AddUserOrKeyPolicy(name, scope)`,
  see `AddNotes`) with a scope from `ApiKeyScopes`. Everything else stays user-only, so keys never
  manage keys or reach admin endpoints.
- Check ownership with the resource policy (`NotePolicies.Owner` is the example) and answer 404,
  not 403, for another user's resource.
- Add an integration test proving one user cannot read or change another user's data.
<!--#if (UseExternal) -->
- Link a provider's login to an existing account only when the provider verified the email
  (`ExternalAuthEndpoints`). Never relax this rule; `ExternalSignInTests` guards it.
<!--#endif -->
<!--#endif -->

## Tests

- Unit tests: `tests/MyProject.Api.UnitTests`, for logic without HTTP.
- Integration tests: `tests/MyProject.Api.IntegrationTests`, through `ApiFactory`, one running API
  per test run. Make test data unique (`Guid.NewGuid()`), because tests share it.
