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

### Retries and failed jobs

A job that throws runs again, up to `Jobs:MaxAttempts` times in all (default 5). The first retry
waits `Jobs:RetryDelaySeconds` (default 10), and each later one twice as long, up to an hour. A job
that fails every attempt is kept as failed and logged as an error; it never retries on its own again.

Once you have fixed the cause, look at failed jobs and run one again (the `jobs.manage` permission,
which the `admin` role has):

```http
GET /v1/admin/jobs/failed?count=50
POST /v1/admin/jobs/failed/{id}/requeue
```

A requeued job gets all its attempts again.
Hangfire keeps failed jobs in the database until someone requeues or deletes them; the dashboard
shows them with their exceptions.

### How they run here: Hangfire

This app runs jobs with [Hangfire](https://www.hangfire.io) (LGPL-3.0, free for commercial use), which
stores them in the app's database. A queued job survives a restart and runs once the app is back.
Hangfire creates its tables (in the `hangfire` schema) when the app first starts, so the database user
needs the right to create them.

| Setting | Default | What it does |
| --- | --- | --- |
| `Jobs:Queue` | `default` | The queue this app puts jobs on and takes them from. |
| `Jobs:RunServer` | `true` | Run jobs in this app. Turn it off where another process runs them. |
| `Jobs:WorkerCount` | `2` | Jobs that run at the same time in this app. |
| `Jobs:PollIntervalSeconds` | `5` | How often an idle server asks the database for new jobs. |
| `Jobs:Dashboard` | `false` (`true` in Development) | Serve the Hangfire dashboard at `/jobs`. |

The dashboard needs the `jobs.manage` permission (the `admin` role has it). A browser reaches it with
a cookie session; with bearer tokens only, use the admin endpoints instead or keep it off.
