using Cronos;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>
/// Queues each <see cref="RecurringJobDefinition"/> at the times its cron schedule gives, in UTC, so it
/// runs and retries like any queued job. Times missed while the app was down are not made up.
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

            var run = new QueuedJob(
                Guid.CreateVersion7().ToString(),
                job.Name,
                (services, token) => ((IRecurringJob)services.GetRequiredService(job.JobType)).RunAsync(token));
            await queue.EnqueueAsync(run, stoppingToken);
            LogQueued(logger, job.Name, run.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {Name} is due; queued as {JobId}")]
    private static partial void LogQueued(ILogger logger, string name, string jobId);
}
