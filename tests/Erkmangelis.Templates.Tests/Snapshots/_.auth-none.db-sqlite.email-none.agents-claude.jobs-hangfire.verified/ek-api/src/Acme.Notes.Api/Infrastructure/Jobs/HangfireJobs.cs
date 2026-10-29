using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Jobs;

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
