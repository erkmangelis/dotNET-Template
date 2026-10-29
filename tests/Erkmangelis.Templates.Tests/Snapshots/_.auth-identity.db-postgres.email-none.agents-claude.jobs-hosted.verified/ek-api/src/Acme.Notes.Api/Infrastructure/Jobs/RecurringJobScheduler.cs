using Cronos;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>
/// Runs each <see cref="RecurringJobDefinition"/> at the times its cron schedule gives, in UTC, each run
/// in its own scope. A run that is still going when the next time comes delays that run; missed times
/// while the app was down are not made up.
/// </summary>
internal sealed partial class RecurringJobScheduler(
    IEnumerable<RecurringJobDefinition> jobs,
    IServiceScopeFactory scopes,
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

            await RunAsync(job, stoppingToken);
        }
    }

    private async Task RunAsync(RecurringJobDefinition job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(job.JobType)).RunAsync(stoppingToken);
            LogSucceeded(logger, job.Name);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailed(logger, job.Name, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {Name} succeeded")]
    private static partial void LogSucceeded(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {Name} failed; it runs again at its next scheduled time")]
    private static partial void LogFailed(ILogger logger, string name, Exception exception);
}
