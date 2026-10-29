# Background work

## Repeating work inside the app

For small housekeeping that every instance may do on its own, derive from `ScopedBackgroundLoop`
(`src/Acme.Notes.Api/Infrastructure/Jobs/ScopedBackgroundLoop.cs`). `IdempotencyCleanup`, which
deletes expired idempotency keys every hour, is the example:

```csharp
internal sealed class ExpiredRowsCleanup(IServiceScopeFactory scopes, TimeProvider time, ILogger<ExpiredRowsCleanup> logger)
    : ScopedBackgroundLoop(scopes, time, logger)
{
    protected override string Name => "expired-rows-cleanup";

    protected override TimeSpan Interval => TimeSpan.FromMinutes(15);

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<IExpiredRows>().DeleteAsync(cancellationToken);
}
```

Register it with `services.AddHostedService<ExpiredRowsCleanup>()`. The loop:

- runs once at startup, then every `Interval`, timed by the injected `TimeProvider` so tests can move time;
- gives each run its own dependency injection scope, so scoped services such as the database context
  are fresh and disposed after the run;
- logs a failed run and tries again on the next tick, instead of stopping the app;
- cancels the run in progress when the app shuts down (within `Api:ShutdownTimeoutSeconds`).

Every instance of the app runs its own loop, and nothing remembers a run across restarts. Keep the work
safe to repeat.

## Jobs

A job is work that runs outside the request that asked for it: an import, a report, a call to a slow
service. The app has two kinds, both in `src/Acme.Notes.Api/Infrastructure/Jobs`:

- **Queued jobs** implement `IJob<TArgs>` and are queued with `IJobQueue.EnqueueAsync<TJob, TArgs>(args)`.
  `ImportNotesJob` is the example: `POST /v1/notes/imports` answers 202 with the job's id, and the job
  creates the notes.
- **Recurring jobs** implement `IRecurringJob` and run on a cron schedule in UTC. Register one with
  `services.AddRecurringJob<TJob>(name, cron)`. `PurgeDeletedNotesJob` is the example: every day at
  03:00 it removes notes deleted more than 30 days ago.

Arguments must survive a round trip through JSON: records of ids and values, with arrays rather than
interface-typed lists. Never pass an entity or a service; look it up in the job. A job class is
resolved from a scope of its own for each run, so it can take the database context or any scoped
service in its constructor.

Change a schedule without a deploy through settings, for example
`Jobs__Schedules__purge-deleted-notes="0 4 * * *"`. Five fields: minute, hour, day of month, month,
day of week.

### Running a job's effect once

Any job can run more than once: a retry after it failed half-way, a second delivery after a crash, a
requeue. Some work is naturally safe to repeat (delete what is expired). For the rest, wrap it in
`JobIdempotency.RunOnceAsync(jobName, key, work)` with a key that names this piece of work, the way
`ImportNotesJob` uses the import's id:

```csharp
await idempotency.RunOnceAsync(nameof(ImportNotesJob), import.ImportId.ToString(), token => ImportAsync(import, token), cancellationToken);
```

A run with a key that already finished does nothing and returns false.
The key and the work's database changes commit in one transaction, so a crash in the middle leaves
neither, and the retry starts clean. Effects outside the database (an email, a payment) are not part
of that transaction: pass the key on to the service that does them if it accepts one.
`JobIdempotencyTests` proves that running an import twice creates its notes once, even when one was
deleted in between.

### Retries and failed jobs

A job that throws runs again, up to `Jobs:MaxAttempts` times in all (default 5). The first retry
waits `Jobs:RetryDelaySeconds` (default 10), and each later one twice as long, up to an hour. A job
that fails every attempt is kept as failed and logged as an error; it never retries on its own again.
Hangfire keeps failed jobs in the database until someone requeues or deletes them; the dashboard
shows them with their exceptions.

### Running once across instances

`IDistributedLock` lets one instance do a piece of work while the others skip it:

```csharp
await using var held = await locks.TryAcquireAsync("reports:monthly", cancellationToken);
if (held is null)
{
    return; // Another instance has it.
}
```

Here it is a lock file next to the database, so it only coordinates processes on the same machine,
which is the only place a SQLite database can be shared anyway.
Hangfire already runs each recurring job once per scheduled time across instances; use the lock for
work that runs outside jobs.

### How they run here: Hangfire

This app runs jobs with [Hangfire](https://www.hangfire.io) (LGPL-3.0, free for commercial use), which
stores them in a SQLite file next to the app's database (`app.db` gets `app-jobs.db`). A queued job
survives a restart and runs once the app is back. Hangfire creates its tables when the app first starts.

| Setting | Default | What it does |
| --- | --- | --- |
| `Jobs:Queue` | `default` | The queue this app puts jobs on and takes them from. |
| `Jobs:RunServer` | `true` | Run jobs in this app. Turn it off where another process runs them. |
| `Jobs:WorkerCount` | `2` | Jobs that run at the same time in this app. |
| `Jobs:PollIntervalSeconds` | `5` | How often an idle server asks the database for new jobs. |
| `Jobs:Dashboard` | `false` (`true` in Development) | Serve the Hangfire dashboard at `/jobs`. |

This API has no sign-in, so the dashboard answers only requests from the same machine. Keep it off
outside Development, or put it behind your reverse proxy's authentication.

## The worker

`src/Acme.Notes.Worker` runs the jobs; the API only queues them (its `Jobs:RunServer` is `false`). The
worker is a small app that references the API project and registers the services jobs need (the
database, jobs, notes) without the HTTP endpoints. It serves `/health/live` and `/health/ready` only.

- Locally: `dotnet run --project src/Acme.Notes.Worker` next to the API. It reads the same user
  secrets as the API.
- In compose and CI: the `worker` image (`docker buildx build --target worker .`), built for amd64
  and arm64 like the API's. `docker compose up` starts it after the migrations.
- More throughput: raise `Jobs:WorkerCount` or run more worker containers; Hangfire hands each job
  to one of them.
- A job's dependencies must be registered in the worker's `Program.cs` as well as the API's. A
  missing one fails the job (and its retries) with an error naming the service.

The integration tests run jobs inside the test API for speed; `WorkerTests` checks that the worker
runs a job the API queued.
