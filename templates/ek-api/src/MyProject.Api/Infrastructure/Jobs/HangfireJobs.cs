using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Infrastructure.Jobs;

/// <summary>Puts jobs on the durable Hangfire queue named by <see cref="JobsOptions.Queue"/>.</summary>
internal sealed class HangfireJobQueue(IBackgroundJobClient client, IOptions<JobsOptions> options) : IJobQueue
{
    public Task<string> EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken)
        where TJob : class, IJob<TArgs> =>
        // Hangfire stores the arguments as JSON and passes its own token for CancellationToken.None.
        Task.FromResult(client.Enqueue<TJob>(options.Value.Queue, job => job.RunAsync(args, CancellationToken.None)));
}

/// <summary>Creates or updates every <see cref="RecurringJobDefinition"/> in Hangfire when the app starts.</summary>
internal sealed class HangfireRecurringJobs(
    IEnumerable<RecurringJobDefinition> jobs,
    IRecurringJobManager manager,
    IOptions<JobsOptions> options) : IHostedService
{
    private static readonly System.Reflection.MethodInfo _run = typeof(IRecurringJob).GetMethod(nameof(IRecurringJob.RunAsync))!;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
        {
            manager.AddOrUpdate(
                job.Name,
                new Job(job.JobType, _run, [CancellationToken.None], options.Value.Queue),
                options.Value.ScheduleFor(job),
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Failed jobs as Hangfire keeps them: in the failed state, until someone requeues or deletes them.</summary>
internal sealed class HangfireFailedJobs(JobStorage storage, IBackgroundJobClient client, IOptions<JobsOptions> options) : IFailedJobs
{
    // Where Hangfire's AutomaticRetryAttribute counts the retries it made.
    private const string RetryCount = "RetryCount";

    public Task<IReadOnlyList<FailedJob>> ListAsync(int count, CancellationToken cancellationToken)
    {
        using var connection = storage.GetConnection();
        IReadOnlyList<FailedJob> failed =
        [
            .. storage.GetMonitoringApi().FailedJobs(0, count).Select(pair => new FailedJob(
                pair.Key,
                pair.Value.Job?.Type.Name ?? "unknown",
                (int.TryParse(connection.GetJobParameter(pair.Key, RetryCount), out var retries) ? retries : 0) + 1,
                new DateTimeOffset(DateTime.SpecifyKind(pair.Value.FailedAt ?? default, DateTimeKind.Utc)),
                $"{pair.Value.ExceptionType}: {pair.Value.ExceptionMessage}")),
        ];
        return Task.FromResult(failed);
    }

    public Task<bool> RequeueAsync(string id, CancellationToken cancellationToken)
    {
        using (var connection = storage.GetConnection())
        {
            if (!IsFailed(connection, id))
            {
                return Task.FromResult(false);
            }

            connection.SetJobParameter(id, RetryCount, "0");
        }

        // Changes only a job that is still failed, so two requeues cannot both run it.
        return Task.FromResult(client.ChangeState(id, new EnqueuedState(options.Value.Queue), FailedState.StateName));
    }

    private static bool IsFailed(IStorageConnection connection, string id)
    {
        try
        {
            return connection.GetJobData(id)?.State == FailedState.StateName;
        }
        catch (FormatException)
        {
            // Each storage has its own id format; an id it cannot read names no job.
            return false;
        }
    }
}
