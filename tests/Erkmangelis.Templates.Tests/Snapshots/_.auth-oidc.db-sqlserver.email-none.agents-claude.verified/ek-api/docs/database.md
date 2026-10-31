# Database

SQL Server 2022 through EF Core and Microsoft.Data.SqlClient. The context is `AppDbContext` in
`src/Acme.Notes.Api/Data`.

## Connection string

`ConnectionStrings:Default` is never stored in an appsettings file.

- Development: `dotnet user-secrets set ConnectionStrings:Default "Server=localhost;Database=app;User Id=sa;Password=<password>;TrustServerCertificate=True" --project src/Acme.Notes.Api`
- Everywhere else: the `ConnectionStrings__Default` environment variable. `compose.yaml` builds it from `MSSQL_SA_PASSWORD` in `.env`.

The app refuses to start without it.

## Local database

```bash
cp .env.example .env            # set MSSQL_ACCEPT_EULA=Y and MSSQL_SA_PASSWORD
docker compose up -d db
```

The compose database listens on `127.0.0.1:1433` only (`MSSQL_PORT` in `.env` changes the port),
so `dotnet run` on the same machine can reach it and the network cannot. The image runs on amd64
only. `MSSQL_PID` picks the edition: `Express` (the default) is free in production up to 10 GB per
database; `Developer` is free but only for development and testing.

In Development the app applies migrations on start (`Database:MigrateOnStartup`) and adds sample
notes (`Database:SeedDevelopmentData`). Neither runs in any other environment unless you turn it on.

## Migrations

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Acme.Notes.Api --context AppDbContext --output-dir Data/Migrations
```

A unit test fails when the model changes without a migration.

## Record metadata, soft delete and audit history

`AuditableEntityInterceptor` (in `Data/`) runs before every save. Opt an entity in with interfaces:

| Interface | What the save does |
| --- | --- |
| `IRecordMetadata` | Sets `CreatedAt` and `UpdatedAt` from `TimeProvider`, and `CreatedBy` and `UpdatedBy` from the signed-in user. |
| `ISoftDeletable` | Turns `Remove` into an update that sets `DeletedAt` and `DeletedBy`. |
| `IAudited` | Writes an `AuditEntry` in the same transaction: the action, each changed property's value before and after, the actor, the time and the trace id. |

A soft-deletable entity also needs a query filter named `SoftDelete.Filter`, as `NoteConfiguration`
has, and its unique indexes filtered to rows that are not deleted. Read deleted rows with
`IgnoreQueryFilters([SoftDelete.Filter])`; restore one by setting `DeletedAt` back to null. A save
that changes nothing writes no audit entry.

Audit entries hold the values they record, so never mark an entity `IAudited` if it keeps secrets in
plain properties, and give audited entities keys you set before saving (Notes use
`Guid.CreateVersion7()`). A note's history is `GET /v1/notes/{id}/history`.
Roles with the `audit.read` permission read every entry at `GET /v1/admin/audit`.

Soft-deleted rows and audit entries are kept until you delete them. Plan a retention rule (for
example a nightly job that purges rows deleted more than 90 days ago) before the tables grow large.

## Applying migrations in production: the migration bundle

The app does not migrate itself in production. The `migrator` stage of the `Dockerfile` builds a
[migration bundle](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles),
a single executable that applies pending migrations and exits. In `compose.yaml` the `migrate`
service runs it after the database is healthy, and the API starts only when it succeeds.

1. Back up first: `./scripts/backup.sh`.
2. `docker compose up -d --build` builds both images, runs `migrate`, then restarts `api`.
3. If `migrate` fails, the old API keeps running: `docker compose logs migrate`.

Write migrations so the previous API version still works against the new schema (add columns as
nullable, backfill, then tighten in a later release). That keeps rollbacks to an image tag change.

## Backups

`scripts/backup.sh` writes a compressed full backup (`BACKUP DATABASE`) to `backups/` and deletes
backups older than 14 days (`KEEP_DAYS`). Run it nightly from cron on the host:

```bash
15 3 * * * cd /srv/acme-notes && ./scripts/backup.sh >> backups/backup.log 2>&1
```

Copy `backups/` off the machine too (for example with `rclone` to object storage): a backup on the
same SD card or disk as the database is not a backup.

Restore into the running database:

```bash
docker compose stop api
docker compose cp backups/<file>.bak db:/var/opt/mssql/data/restore.bak
docker compose exec db bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q \
  "ALTER DATABASE [app] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [app] FROM DISK = N'\''/var/opt/mssql/data/restore.bak'\'' WITH REPLACE; ALTER DATABASE [app] SET MULTI_USER"'
docker compose start api
```

Test a restore every few months on a scratch machine.
