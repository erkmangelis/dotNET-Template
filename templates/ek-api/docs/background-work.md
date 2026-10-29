# Background work

## Repeating work inside the app

For small housekeeping that every instance may do on its own, derive from `ScopedBackgroundLoop`
(`src/MyProject.Api/Infrastructure/Jobs/ScopedBackgroundLoop.cs`). `IdempotencyCleanup`, which
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
<!--#if (UseJobs) -->

## Jobs

A job is work that runs outside the request that asked for it: an import, a report, a call to a slow
service. The app has two kinds, both in `src/MyProject.Api/Infrastructure/Jobs`:

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
<!--#if (UseDatabase) -->
The key and the work's database changes commit in one transaction, so a crash in the middle leaves
neither, and the retry starts clean. Effects outside the database (an email, a payment) are not part
of that transaction: pass the key on to the service that does them if it accepts one.
<!--#else -->
If the work throws, the key is dropped so the retry runs it. Without a database the keys are kept in
memory, so they are forgotten when the app stops.
<!--#endif -->
`JobIdempotencyTests` proves that running an import twice creates its notes once, even when one was
deleted in between.

### Retries and failed jobs

A job that throws runs again, up to `Jobs:MaxAttempts` times in all (default 5). The first retry
waits `Jobs:RetryDelaySeconds` (default 10), and each later one twice as long, up to an hour. A job
that fails every attempt is kept as failed and logged as an error; it never retries on its own again.
<!--#if (UseAuth) -->

Once you have fixed the cause, look at failed jobs and run one again (the `jobs.manage` permission,
which the `admin` role has):

```http
GET /v1/admin/jobs/failed?count=50
POST /v1/admin/jobs/failed/{id}/requeue
```

A requeued job gets all its attempts again.
<!--#endif -->
<!--#if (UseHostedJobs) -->
In this app failed jobs are kept in memory, the last 100 of them, until they are requeued or the app
stops.
<!--#endif -->
<!--#if (UseHangfire) -->
Hangfire keeps failed jobs in the database until someone requeues or deletes them; the dashboard
shows them with their exceptions.
<!--#endif -->
<!--#if (UseHostedJobs) -->

### How they run here: in process

This app runs jobs on hosted services (`--jobs hosted`): `InProcessJobQueue` keeps queued jobs in
memory and `InProcessJobRunner` runs `Jobs:WorkerCount` of them at a time; `RecurringJobScheduler`
starts recurring jobs at their times. It needs no database and no extra service, but:

- jobs still queued when the app stops are lost, and so is a job interrupted by shutdown;
- each instance runs its own queue and its own schedule;
- a time missed while the app was down is not made up.

When a job must survive a restart, generate with `--jobs hangfire` and copy your jobs over: the
`IJob`, `IRecurringJob` and `IJobQueue` contracts are the same.
<!--#endif -->
<!--#if (UseHangfire) -->

### How they run here: Hangfire

<!--#if (UseSqlite) -->
This app runs jobs with [Hangfire](https://www.hangfire.io) (LGPL-3.0, free for commercial use), which
stores them in a SQLite file next to the app's database (`app.db` gets `app-jobs.db`). A queued job
survives a restart and runs once the app is back. Hangfire creates its tables when the app first starts.
<!--#else -->
This app runs jobs with [Hangfire](https://www.hangfire.io) (LGPL-3.0, free for commercial use), which
stores them in the app's database. A queued job survives a restart and runs once the app is back.
Hangfire creates its tables (in the `hangfire` schema) when the app first starts, so the database user
needs the right to create them.
<!--#endif -->

| Setting | Default | What it does |
| --- | --- | --- |
| `Jobs:Queue` | `default` | The queue this app puts jobs on and takes them from. |
| `Jobs:RunServer` | `true` | Run jobs in this app. Turn it off where another process runs them. |
| `Jobs:WorkerCount` | `2` | Jobs that run at the same time in this app. |
| `Jobs:PollIntervalSeconds` | `5` | How often an idle server asks the database for new jobs. |
| `Jobs:Dashboard` | `false` (`true` in Development) | Serve the Hangfire dashboard at `/jobs`. |

<!--#if (UseAuth) -->
The dashboard needs the `jobs.manage` permission (the `admin` role has it). A browser reaches it with
a cookie session; with bearer tokens only, use the admin endpoints instead or keep it off.
<!--#else -->
This API has no sign-in, so the dashboard answers only requests from the same machine. Keep it off
outside Development, or put it behind your reverse proxy's authentication.
<!--#endif -->
<!--#endif -->
<!--#endif -->
