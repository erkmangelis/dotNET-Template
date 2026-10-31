# Database

PostgreSQL 18 through EF Core and Npgsql. The context is `AppDbContext` in `src/Acme.Notes.Api/Data`.

## Connection string

`ConnectionStrings:Default` is never stored in an appsettings file.

- Development: `dotnet user-secrets set ConnectionStrings:Default "Host=localhost;Database=app;Username=app;Password=<password>" --project src/Acme.Notes.Api`
- Everywhere else: the `ConnectionStrings__Default` environment variable. `compose.yaml` builds it from `POSTGRES_PASSWORD` in `.env`.

The app refuses to start without it.

## Local database

```bash
cp .env.example .env            # set POSTGRES_PASSWORD
docker compose up -d db
```

The compose database listens on `127.0.0.1:5432` only (`POSTGRES_PORT` in `.env` changes the port),
so `dotnet run` on the same machine can reach it and the network cannot.

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
| `IRecordMetadata` | Sets `CreatedAt` and `UpdatedAt` from `TimeProvider`. |
| `ISoftDeletable` | Turns `Remove` into an update that sets `DeletedAt`. |
| `IAudited` | Writes an `AuditEntry` in the same transaction: the action, each changed property's value before and after, the time and the trace id. |

A soft-deletable entity also needs a query filter named `SoftDelete.Filter`, as `NoteConfiguration`
has, and its unique indexes filtered to rows that are not deleted. Read deleted rows with
`IgnoreQueryFilters([SoftDelete.Filter])`; restore one by setting `DeletedAt` back to null. A save
that changes nothing writes no audit entry.

Audit entries hold the values they record, so never mark an entity `IAudited` if it keeps secrets in
plain properties, and give audited entities keys you set before saving (Notes use
`Guid.CreateVersion7()`). A note's history is `GET /v1/notes/{id}/history`.

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

`scripts/backup.sh` writes a compressed `pg_dump` to `backups/` and deletes dumps older than
14 days (`KEEP_DAYS`). Run it nightly from cron on the host:

```bash
15 3 * * * cd /srv/acme-notes && ./scripts/backup.sh >> backups/backup.log 2>&1
```

Copy `backups/` off the machine too (for example with `rclone` to object storage): a backup on the
same SD card or disk as the database is not a backup.

Restore into the running database:

```bash
docker compose exec -T db pg_restore --username app --dbname app --clean --if-exists < backups/<file>.dump
```

Test a restore every few months on a scratch machine.
