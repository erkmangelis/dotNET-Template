# Local development with .NET Aspire

`src/Acme.Notes.AppHost` is a [.NET Aspire](https://aspire.dev) AppHost (MIT licence). It starts the
apps and their dependencies for local development and opens a dashboard with their logs, traces and
metrics. It is not deployed: production runs from `compose.yaml` (see `docs/deployment.md`).

```bash
dotnet run --project src/Acme.Notes.AppHost
```

Docker must be running. The console prints the dashboard's login link.

What it starts:

- `postgres`: a PostgreSQL container with a data volume, and the `app` database. The apps get its
  connection string as `ConnectionStrings:Default`, so they need no user secret for it.
- `api`: the API, in Development, which applies migrations when it starts. Aspire waits for
  `/health/ready` before it marks it healthy.
- `worker`: the worker, started once the API is up (so the migrations have run).

The apps send telemetry to the dashboard because Aspire sets `OTEL_EXPORTER_OTLP_ENDPOINT`, which
`Infrastructure/Observability.cs` already honours; no Aspire package is added to the apps.

The AppHost runs with `dotnet run` and the dashboard and orchestrator packages from NuGet, so the
Aspire CLI is optional. Keep the version in `Acme.Notes.AppHost.csproj` (`Aspire.AppHost.Sdk/…`)
equal to the `Aspire.Hosting.*` versions in `Directory.Packages.props`.
