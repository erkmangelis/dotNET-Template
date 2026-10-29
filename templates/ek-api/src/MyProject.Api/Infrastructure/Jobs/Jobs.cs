namespace MyProject.Api.Infrastructure.Jobs;

/// <summary>
/// Work that runs outside the request that asked for it. Queue it with <see cref="IJobQueue"/>.
/// <typeparamref name="TArgs"/> must survive a round trip through JSON, because a durable queue
/// stores it: use a record of ids and values, never an entity or a service.
/// </summary>
/// <remarks>
/// A job can run more than once (a retry, or a second delivery after a crash), so make it safe to
/// repeat. Register the class in dependency injection; each run resolves it from a scope of its own.
/// </remarks>
internal interface IJob<in TArgs>
{
    Task RunAsync(TArgs args, CancellationToken cancellationToken);
}

/// <summary>Work that runs on a cron schedule. Register it with <see cref="JobsSetup.AddRecurringJob{TJob}"/>.</summary>
internal interface IRecurringJob
{
    Task RunAsync(CancellationToken cancellationToken);
}

internal interface IJobQueue
{
    /// <summary>Queues <typeparamref name="TJob"/> to run with <paramref name="args"/>, and returns the job's id.</summary>
    Task<string> EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken)
        where TJob : class, IJob<TArgs>;
}

/// <summary>Jobs that used up their attempts, and a way to run one again once its cause is fixed.</summary>
internal interface IFailedJobs
{
    /// <summary>Up to <paramref name="count"/> failed jobs, newest first.</summary>
    Task<IReadOnlyList<FailedJob>> ListAsync(int count, CancellationToken cancellationToken);

    /// <summary>Queues a failed job again with all its attempts. False when no failed job has that id.</summary>
    Task<bool> RequeueAsync(string id, CancellationToken cancellationToken);
}

/// <param name="Id">The job's id, as <see cref="IJobQueue.EnqueueAsync{TJob, TArgs}"/> returned it.</param>
/// <param name="Name">The job class.</param>
/// <param name="Attempts">How many times it ran.</param>
/// <param name="FailedAt">When the last attempt failed.</param>
/// <param name="Error">The last exception's type and message.</param>
public sealed record FailedJob(string Id, string Name, int Attempts, DateTimeOffset FailedAt, string Error);

/// <summary>A recurring job.</summary>
/// <param name="Name">Unique among recurring jobs; it names the job in logs, settings and the dashboard.</param>
/// <param name="JobType">The <see cref="IRecurringJob"/> class.</param>
/// <param name="Cron">The default schedule in UTC, in five fields: minute, hour, day of month, month, day of week. <c>Jobs:Schedules:{Name}</c> overrides it.</param>
internal sealed record RecurringJobDefinition(string Name, Type JobType, string Cron);
