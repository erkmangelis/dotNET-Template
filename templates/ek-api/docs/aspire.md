# Local development with .NET Aspire

`src/MyProject.AppHost` is a [.NET Aspire](https://aspire.dev) AppHost (MIT licence). It starts the
apps and their dependencies for local development and opens a dashboard with their logs, traces and
metrics. It is not deployed: production runs from `compose.yaml` (see `docs/deployment.md`).

```bash
dotnet run --project src/MyProject.AppHost
```

Docker must be running. The console prints the dashboard's login link.

What it starts:

<!--#if (UsePostgres) -->
- `postgres`: a PostgreSQL container with a data volume, and the `app` database. The apps get its
  connection string as `ConnectionStrings:Default`, so they need no user secret for it.
<!--#elif (UseSqlServer) -->
- `sqlserver`: a SQL Server container with a data volume, and the `app` database. The apps get its
  connection string as `ConnectionStrings:Default`, so they need no user secret for it. Running it
  means you accept the SQL Server licence, as with compose.
<!--#elif (UseSqlite) -->
- No database container: the API uses its SQLite file (`appsettings.Development.json`).
<!--#endif -->
<!--#if (UseDatabase) -->
- `api`: the API, in Development, which applies migrations when it starts. Aspire waits for
  `/health/ready` before it marks it healthy.
<!--#else -->
- `api`: the API, in Development. Aspire waits for `/health/ready` before it marks it healthy.
<!--#endif -->
<!--#if (UseWorker) -->
- `worker`: the worker, started once the API is up (so the migrations have run).
<!--#endif -->

The apps send telemetry to the dashboard because Aspire sets `OTEL_EXPORTER_OTLP_ENDPOINT`, which
`Infrastructure/Observability.cs` already honours; no Aspire package is added to the apps.

The AppHost runs with `dotnet run` and the dashboard and orchestrator packages from NuGet, so the
Aspire CLI is optional. Keep the version in `MyProject.AppHost.csproj` (`Aspire.AppHost.Sdk/…`)
equal to the `Aspire.Hosting.*` versions in `Directory.Packages.props`.
