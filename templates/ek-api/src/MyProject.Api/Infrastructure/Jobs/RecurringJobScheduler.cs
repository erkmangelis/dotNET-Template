using System.Globalization;
using Cronos;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Infrastructure.Jobs;

/// <summary>
/// Queues each <see cref="RecurringJobDefinition"/> at the times its cron schedule gives, in UTC, so it
/// runs and retries like any queued job. Every instance of the app does this; <see cref="RecurringJobRunner"/>
/// makes sure only one of them runs each time. Times missed while the app was down are not made up.
/// </summary>
internal sealed partial class RecurringJobScheduler(
    IEnumerable<RecurringJobDefinition> jobs,
    InProcessJobQueue queue,
    TimeProvider time,
    IOptions<JobsOptions> options,
    ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    // Task.Delay takes at most about 49 days, so long waits are made of shorter ones.
    private static readonly TimeSpan _longestWait = TimeSpan.FromDays(1);

    public static bool IsValidCron(string cron) => CronExpression.TryParse(cron, out _);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(jobs.Select(job => ScheduleAsync(job, stoppingToken)));

    private async Task ScheduleAsync(RecurringJobDefinition job, CancellationToken stoppingToken)
    {
        var schedule = CronExpression.Parse(options.Value.ScheduleFor(job));
        while (schedule.GetNextOccurrence(time.GetUtcNow(), TimeZoneInfo.Utc) is { } next)
        {
            for (var now = time.GetUtcNow(); now < next; now = time.GetUtcNow())
            {
                var wait = next - now;
                await Task.Delay(wait < _longestWait ? wait : _longestWait, time, stoppingToken);
            }

            var scheduledAt = next;
            var run = new QueuedJob(
                Guid.CreateVersion7().ToString(),
                job.Name,
                (services, token) => services.GetRequiredService<RecurringJobRunner>().RunAsync(job, scheduledAt, token));
            await queue.EnqueueAsync(run, stoppingToken);
            LogQueued(logger, job.Name, run.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {Name} is due; queued as {JobId}")]
    private static partial void LogQueued(ILogger logger, string name, string jobId);
}

/// <summary>
/// Runs one scheduled time of a recurring job on one instance only, and once: the instance that takes
/// the job's <see cref="IDistributedLock"/> runs it, and <see cref="JobIdempotency"/> keyed by the
/// scheduled time stops an instance that comes later from running it again.
/// </summary>
internal sealed partial class RecurringJobRunner(
    IServiceProvider services,
    IDistributedLock locks,
    JobIdempotency idempotency,
    ILogger<RecurringJobRunner> logger)
{
    public async Task RunAsync(RecurringJobDefinition job, DateTimeOffset scheduledAt, CancellationToken cancellationToken)
    {
        var name = $"recurring:{job.Name}";
        await using var held = await locks.TryAcquireAsync(name, cancellationToken);
        if (held is null)
        {
            LogRunningElsewhere(logger, job.Name, scheduledAt);
            return;
        }

        var ran = await idempotency.RunOnceAsync(
            name,
            scheduledAt.ToString("O", CultureInfo.InvariantCulture),
            token => ((IRecurringJob)services.GetRequiredService(job.JobType)).RunAsync(token),
            cancellationToken);
        if (!ran)
        {
            LogAlreadyRan(logger, job.Name, scheduledAt);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {Name} for {ScheduledAt} is running on another instance")]
    private static partial void LogRunningElsewhere(ILogger logger, string name, DateTimeOffset scheduledAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {Name} for {ScheduledAt} already ran")]
    private static partial void LogAlreadyRan(ILogger logger, string name, DateTimeOffset scheduledAt);
}
