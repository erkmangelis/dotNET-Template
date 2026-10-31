# R04, Jobs, worker and Aspire (0.4.0): why background work works like this

Until R04, everything happened inside a request: the client waited while the API worked. R04 adds
work that happens *outside* a request: a repeating housekeeping loop, queued jobs (`IJob<TArgs>`),
cron-scheduled recurring jobs, retries, idempotent job effects, distributed locks, an optional
separate worker process (`--worker`) and a .NET Aspire AppHost for local development (`--aspire`).

Background work is where distributed-systems problems show up first, even in a small app: things
run twice, run in two places at once, or get lost on restart. This doc is mostly about those three
problems.

## Contents

1. [Three kinds of background work](#1-three-kinds-of-background-work)
2. [`ScopedBackgroundLoop`: the hosted service pattern done right](#2-scopedbackgroundloop-the-hosted-service-pattern-done-right)
3. [One job contract, two engines](#3-one-job-contract-two-engines)
4. [`hosted`: an in-process queue on channels](#4-hosted-an-in-process-queue-on-channels)
5. [`hangfire`: durable jobs in your database](#5-hangfire-durable-jobs-in-your-database)
6. [Retries with backoff, then a human](#6-retries-with-backoff-then-a-human)
7. [At-least-once delivery and `JobIdempotency`](#7-at-least-once-delivery-and-jobidempotency)
8. [Distributed locks](#8-distributed-locks)
9. [`--worker`: splitting the process](#9---worker-splitting-the-process)
10. [`--aspire`: local orchestration](#10---aspire-local-orchestration)
11. [Lessons from testing: static state](#11-lessons-from-testing-static-state)

---

## 1. Three kinds of background work

| Kind | Example | Must survive a restart? | Must run once across instances? |
| --- | --- | --- | --- |
| Housekeeping loop | Delete expired idempotency keys | No (it runs again soon) | No (deleting twice is harmless) |
| Queued job | Import 100 notes after `POST /v1/notes/imports` answers 202 | Ideally yes | Yes |
| Recurring job | Purge notes deleted 30+ days ago, daily at 03:00 | It must not be skipped | Yes |

**Why separate them.** Using a full job system for housekeeping is overkill; using a simple loop
for a queued import loses work on every deploy. The template offers the right tool for each and
says in the docs which one to reach for.

## 2. `ScopedBackgroundLoop`: the hosted service pattern done right

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Jobs/ScopedBackgroundLoop.cs (trimmed)
internal abstract partial class ScopedBackgroundLoop(IServiceScopeFactory scopes, TimeProvider time, ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await RunInScopeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunInScopeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await RunOnceAsync(scope.ServiceProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogFailed(logger, Name, Interval, exception); }
    }
}
```

Every line fixes a classic `BackgroundService` bug:

- **A new DI scope per run.** A hosted service is a singleton. Injecting `AppDbContext` (scoped)
  directly either fails or, worse, keeps one context alive forever, growing its change tracker and
  holding stale data. A fresh scope per run gives each run a fresh context.
- **Catch and log, keep looping.** In .NET 6+, an unhandled exception in `ExecuteAsync` stops the
  whole host by default. One bad run should not take down the API.
- **But let cancellation through.** On shutdown, cancellation must end the loop, not be logged as a
  failure.
- **`PeriodicTimer` with `TimeProvider`.** No drift from `Task.Delay` arithmetic, and tests can
  advance a fake clock instead of waiting real minutes.

**Trade-off.** Every instance of the app runs its own loop. That is fine for work that is safe to
repeat, which is why the doc comment says so, and why anything else is a job.

## 3. One job contract, two engines

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Jobs/Jobs.cs
/// TArgs must survive a round trip through JSON, because a durable queue stores it: use a record
/// of ids and values, never an entity or a service.
/// A job can run more than once (a retry, or a second delivery after a crash), so make it safe to repeat.
internal interface IJob<in TArgs>
{
    Task RunAsync(TArgs args, CancellationToken cancellationToken);
}

internal interface IJobQueue
{
    Task<string> EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken)
        where TJob : class, IJob<TArgs>;
}
```

**Why our own interfaces over Hangfire's API.** Feature code (`ImportNotesJob`) depends on
`IJob<TArgs>` and `IJobQueue`, not on Hangfire. The same feature code works with `--jobs hosted`
and `--jobs hangfire`, tests can swap the engine, and moving to another engine later touches one
folder. This is one of the few places where an abstraction over a library pays off, because there
really are two implementations from day one.

**Why arguments are plain records.** A durable queue serializes arguments to JSON and runs the job
later, maybe in another process. An entity would be stale by then; a service cannot be serialized
at all. Pass ids, and load fresh data inside the job.

## 4. `hosted`: an in-process queue on channels

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Jobs/InProcessJobQueue.cs
private readonly Channel<QueuedJob> _jobs = Channel.CreateBounded<QueuedJob>(
    new BoundedChannelOptions(options.Value.QueueCapacity) { FullMode = BoundedChannelFullMode.Wait });
```

**The choice.** `System.Threading.Channels` as the queue, `Jobs:WorkerCount` runner tasks reading
from it, and Cronos (MIT) to parse cron expressions for recurring jobs.

**Why channels.** They are the built-in, high-performance producer/consumer queue in .NET. **Bounded**
with `Wait` gives *back-pressure*: if jobs come in faster than they run, enqueueing waits instead of
growing memory until the process dies.

**Why Cronos.** Parsing cron correctly (time zones, daylight saving, "last day of month") is
fiddly. Cronos is small, focused and MIT. Quartz.NET would do it too, but it is a full scheduler,
much bigger than we need for parsing.

**Trade-off, stated plainly.** Queued jobs live in memory and are **lost on restart**. The README
and `docs/background-work.md` say so. It is right for a single small box (the `single-box`
preset) where a lost import is retried by the user, and wrong for anything that must not be lost.

## 5. `hangfire`: durable jobs in your database

**The choice.** Hangfire 1.8 storing jobs in the same database as the app (PostgreSQL, SQL Server
or a SQLite file next to the database), with its dashboard at `/jobs` behind the `jobs.manage`
permission.

**Why Hangfire.** It is the most widely used .NET job library, it stores jobs in a database you
already run (no Redis or RabbitMQ to operate), retries and recurring jobs are built in, and the
dashboard answers "what happened to my job?" without writing anything.

**The licence.** Hangfire core is **LGPL-3.0**, which allows commercial use when you use it as a
library without modifying it. Hangfire **Pro** (batches, extra storage) is paid and deliberately
not used, per the licence rule from R01. A comment in `Directory.Packages.props` records this so
nobody adds Pro by accident.

**Alternatives considered.**

| Option | Why not the default |
| --- | --- |
| Quartz.NET | A scheduler first; queued jobs, retries and a dashboard need more work. |
| A message broker (RabbitMQ, Azure Service Bus) | Another server to run. R09 is planned to add messaging for systems that need it. |
| MassTransit | v9 went commercial. |
| TickerQ, Coravel | Younger or lighter; fewer people know them. Coravel has no durable queue. |

**Why `hangfire` turns PostgreSQL on.** Durable jobs need durable storage; same computed-symbol
trick as R02's auth.

**A queued job survives a restart.** There is a test for exactly that (`JobRestartTests`). It is
the difference between `hosted` and `hangfire`, so it is tested, not just promised.

## 6. Retries with backoff, then a human

A failing job runs up to `Jobs:MaxAttempts` times, waiting longer each time (exponential backoff
from `Jobs:RetryDelaySeconds`), then stays **failed** and is logged as an error. Admins can list
failed jobs (`GET /v1/admin/jobs/failed`) and requeue one after fixing the cause.

**Why bounded.** Hangfire's default is 10 retries spread over hours. That hides bugs: a job that
can never succeed retries quietly for a day. A small bound plus an error log plus an admin endpoint
means a human finds out.

**Why backoff.** If a job fails because a service is down, retrying immediately just hits it again.
Waiting longer each time gives it room to recover.

**Why an API and not only the dashboard.** The `hosted` engine has no dashboard, and an API can be
automated (alerts, scripts). Both engines implement the same `IFailedJobs`.

## 7. At-least-once delivery and `JobIdempotency`

**The fact to internalise.** Every practical job system delivers **at least once**, never exactly
once. A job can run twice: a retry after a timeout that actually succeeded, a crash after the work
but before the "done" mark, an admin requeue. You cannot prevent duplicates; you make them harmless.

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Jobs/JobIdempotency.cs (trimmed)
/// The key and every change the work saves through the app's database context commit in one
/// transaction: either both are saved or neither is.
public async Task<bool> RunOnceAsync(string jobName, string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var record = new IdempotencyRecord { Scope = $"job:{jobName}", Key = key, ... };
    if (await store.TryReserveAsync(record, cancellationToken) is { } existing)
    {
        return existing.StatusCode is not null ? false : throw new InvalidOperationException("... running elsewhere");
    }
    ...
}
```

**Why reuse R03's idempotency store.** It already does "reserve a key, remember the result, expire
it later". Jobs just use a different scope (`job:{name}`).

**Why one transaction.** If the key were saved separately from the work, a crash between the two
leaves either "done but not marked" (runs again: duplicate) or "marked but not done" (never runs:
lost). In one transaction there is no in-between.

**The limit, stated in the code.** The transaction covers only the database. Sending an email or
calling another service inside the job is not rolled back. For those, pass the key on to the other
service if it accepts one (most payment APIs do).

## 8. Distributed locks

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Jobs/DistributedLock.cs (PostgreSQL, trimmed)
// Advisory locks take a 64-bit key: the first 8 bytes of the name's SHA-256.
var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
```

**The problem.** Run two API instances with `--jobs hosted` and both schedulers fire the 03:00
purge. Something must make one of them skip.

**The choice.** `IDistributedLock`, implemented with what each database already offers:

| Provider | Mechanism |
| --- | --- |
| PostgreSQL | session advisory locks (`pg_try_advisory_lock`) |
| SQL Server | application locks (`sp_getapplock`) |
| SQLite | a lock file next to the database (one machine anyway) |
| no database | an in-process lock |

**Why database locks, not Redis.** You already run the database; adding Redis just for locks is
another server to operate. Database session locks have a great property: **if the app dies, the
database ends the session and the lock goes with it.** A lock row in a table would stay locked
until someone noticed.

**Lock plus idempotency.** The hosted scheduler takes the lock *and* records the scheduled time
with `JobIdempotency`. The lock stops two instances running at the same moment; the idempotency
record stops a second instance running the same 03:00 slot a moment later, after the first released
the lock.

**Alternatives.** The `DistributedLock` NuGet package (MIT, many backends) is good; the template
needs four small implementations and keeps them visible. Hangfire has its own locking for its
recurring jobs, so this matters mainly for `hosted`.

## 9. `--worker`: splitting the process

```csharp
// templates/ek-api/src/MyProject.Worker/Program.cs
// Runs the jobs the API queues, in its own process: the API keeps Jobs:RunServer off and this
// process runs the Hangfire server. It serves only the health endpoints.
builder.AddObservability();
builder.AddDatabase();
builder.AddJobs();
builder.Services.AddNotes();
```

**Why a separate worker.** A CPU-heavy job (a big import, image processing in R06) in the API
process steals threads and memory from requests. A separate process scales on its own (one API,
three workers), deploys on its own, and a crash in a job does not restart the API.

**Why it implies Hangfire.** The worker and API are separate processes; the API's queue must be
somewhere both can see. The in-memory queue cannot cross processes, a database-backed one can.

**Why the worker references the API project.** There is no shared "core" project yet (R01 chose one
project; R09 is planned to add layered layouts). Referencing the API reuses its job classes,
`AppDbContext` and setup methods. It was proposed on a decision card in the R04 thread. The cost is
visible: the worker carries the API's assembly, and its csproj needs
`ErrorOnDuplicatePublishOutputFiles=false` because both projects publish an `appsettings.json`.

**Why not a separate `ek-worker` template.** It would duplicate everything the API template has.
`ek-api --worker` keeps one template; R05 added an `ek-worker` *preset alias* for discoverability.

**Images.** The Dockerfile gains a `worker` target from the same build stage, so the API and worker
are always built from the same source. CI builds both for amd64 and arm64.

**Graceful shutdown.** The worker waits up to 30 seconds for a running job to finish when stopped,
and compose waits 35, so a deploy does not kill jobs mid-way.

## 10. `--aspire`: local orchestration

```csharp
// templates/ek-api/src/MyProject.AppHost/AppHost.cs (trimmed)
var database = builder.AddPostgres("postgres").WithDataVolume().AddDatabase("Default", databaseName: "app");
var api = builder.AddProject<Projects.MyProject_Api>("api")
    .WithReference(database).WaitFor(database)
    .WithHttpHealthCheck("/health/ready");
builder.AddProject<Projects.MyProject_Worker>("worker").WithReference(database).WaitFor(api);
```

**What Aspire is.** A .NET-native way to describe your local system in C#: which containers and
projects to start, in what order, with which connection strings. `dotnet run` on the AppHost starts
everything and opens a dashboard with logs, traces and metrics from all of them.

**Why it was moved from R08 to R04.** The research for the plan found it is most useful exactly when
there is more than one process. R04 is when the worker arrives, so that is when Aspire pays off.

**Why it is local only.** Compose stays the deployment story (R01). Aspire can deploy too, but
mainly to Azure and Kubernetes; the target here is a Pi or a VPS.

**Why the dashboard "just works".** R01 already exports OpenTelemetry over OTLP when the endpoint
is set. Aspire sets it. No extra code: a nice payoff from choosing the standard in R01.

**Details:** Aspire is MIT-licensed. `NoWarn ASPIRE010` lets the AppHost run with plain
`dotnet run` without the Aspire CLI. The generated `Projects.MyProject_Api` class name contains the
project name with dots turned into underscores, so a computed symbol renames `MyProject_` to, say,
`Acme_Notes_`.

## 11. Lessons from testing: static state

Hangfire keeps some state in **static** fields (its log provider, its global retry filter, its job
activator). Static state is shared by everything in the process. Tests run several API instances in
one process (`WebApplicationFactory` per test class), and that broke in three ways while building
R04 and R05:

- The log provider pointed at a disposed logger factory after a test host shut down.
- The global retry filter took whichever settings the last host set, so a test expecting 2 attempts
  got 10 and timed out.
- `JobActivator.Current` pointed at the last app's service provider; jobs in another test host
  resolved services from a disposed container (`ObjectDisposedException`). The fix registers a
  per-app `AspNetCoreJobActivator`.

**The lesson.** Avoid static mutable state in your own code, and when a library has it, give each
app its own instance explicitly. It is also why the template's own abstractions take everything
through dependency injection.

---

## Try it

1. Generate with `--jobs hosted`, `POST /v1/notes/imports`, and watch the log as the job runs.
   Restart the app mid-import and see the job disappear. Now try `--jobs hangfire`.
2. Make `ImportNotesJob` throw. Watch the retries, then find the job in
   `GET /v1/admin/jobs/failed` and requeue it.
3. Generate the `api-worker` preset and run `dotnet run --project src/<Name>.AppHost`. Open the
   Aspire dashboard and find the API's and the worker's logs side by side.
4. Read `JobIdempotencyTests.cs` and work out which failure each test simulates.

## Further reading

- Background tasks with hosted services: https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
- System.Threading.Channels: https://learn.microsoft.com/dotnet/core/extensions/channels
- PostgreSQL advisory locks: https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS
- .NET Aspire overview: https://learn.microsoft.com/dotnet/aspire/get-started/aspire-overview
