using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Infrastructure.Jobs;

/// <summary>One queued job: what to run, resolved from the scope it runs in, and which attempt this is.</summary>
internal sealed record QueuedJob(string Id, string Name, Func<IServiceProvider, CancellationToken, Task> RunAsync)
{
    public int Attempt { get; init; } = 1;
}

/// <summary>
/// Keeps queued jobs in process memory for <see cref="InProcessJobRunner"/>. Fast and simple, but jobs
/// still waiting when the app stops are lost, and only this instance runs them. When a job must
/// survive a restart, use a durable store: see docs/background-work.md.
/// </summary>
internal sealed class InProcessJobQueue(IOptions<JobsOptions> options) : IJobQueue
{
    private readonly Channel<QueuedJob> _jobs = Channel.CreateBounded<QueuedJob>(
        new BoundedChannelOptions(options.Value.QueueCapacity) { FullMode = BoundedChannelFullMode.Wait });

    public ChannelReader<QueuedJob> Reader => _jobs.Reader;

    public async Task<string> EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken)
        where TJob : class, IJob<TArgs>
    {
        var job = new QueuedJob(
            Guid.CreateVersion7().ToString(),
            typeof(TJob).Name,
            (services, token) => services.GetRequiredService<TJob>().RunAsync(args, token));
        await EnqueueAsync(job, cancellationToken);
        return job.Id;
    }

    public ValueTask EnqueueAsync(QueuedJob job, CancellationToken cancellationToken) => _jobs.Writer.WriteAsync(job, cancellationToken);
}

/// <summary>
/// Runs queued jobs, <see cref="JobsOptions.WorkerCount"/> at a time, each in its own scope. A job that
/// throws runs again after a growing wait, up to <see cref="JobsOptions.MaxAttempts"/> times in all;
/// then it goes to <see cref="InProcessFailedJobs"/>.
/// </summary>
internal sealed partial class InProcessJobRunner(
    InProcessJobQueue queue,
    InProcessFailedJobs failures,
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<JobsOptions> options,
    ILogger<InProcessJobRunner> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Value.WorkerCount).Select(_ => WorkAsync(stoppingToken)));

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await RunAsync(job, stoppingToken);
        }
    }

    private async Task RunAsync(QueuedJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await job.RunAsync(scope.ServiceProvider, stoppingToken);
            LogSucceeded(logger, job.Name, job.Id, job.Attempt);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogAbandoned(logger, job.Name, job.Id);
        }
        catch (Exception exception)
        {
            if (job.Attempt < options.Value.MaxAttempts)
            {
                var delay = options.Value.RetryDelayBefore(job.Attempt + 1);
                LogRetrying(logger, job.Name, job.Id, job.Attempt, delay, exception);
                _ = RetryLaterAsync(job with { Attempt = job.Attempt + 1 }, delay, stoppingToken);
            }
            else
            {
                failures.Add(job, exception, time.GetUtcNow());
                LogFailed(logger, job.Name, job.Id, job.Attempt, exception);
            }
        }
    }

    private async Task RetryLaterAsync(QueuedJob job, TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, time, stoppingToken);
            await queue.EnqueueAsync(job, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            LogAbandoned(logger, job.Name, job.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Name} {JobId} succeeded on attempt {Attempt}")]
    private static partial void LogSucceeded(ILogger logger, string name, string jobId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Name} {JobId} failed on attempt {Attempt}; it runs again in {Delay}")]
    private static partial void LogRetrying(ILogger logger, string name, string jobId, int attempt, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Name} {JobId} was stopped by shutdown and is lost")]
    private static partial void LogAbandoned(ILogger logger, string name, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Name} {JobId} failed after {Attempts} attempts")]
    private static partial void LogFailed(ILogger logger, string name, string jobId, int attempts, Exception exception);
}

/// <summary>
/// The last <see cref="Capacity"/> jobs that used up their attempts, kept in memory until they are
/// requeued or the app stops.
/// </summary>
internal sealed class InProcessFailedJobs(InProcessJobQueue queue) : IFailedJobs
{
    public const int Capacity = 100;

    private readonly Lock _lock = new();
    private readonly LinkedList<(QueuedJob Job, FailedJob Failure)> _failed = [];

    public void Add(QueuedJob job, Exception exception, DateTimeOffset failedAt)
    {
        lock (_lock)
        {
            _failed.AddFirst((job, new FailedJob(job.Id, job.Name, job.Attempt, failedAt, $"{exception.GetType().Name}: {exception.Message}")));
            if (_failed.Count > Capacity)
            {
                _failed.RemoveLast();
            }
        }
    }

    public Task<IReadOnlyList<FailedJob>> ListAsync(int count, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<FailedJob>>([.. _failed.Take(count).Select(entry => entry.Failure)]);
        }
    }

    public async Task<bool> RequeueAsync(string id, CancellationToken cancellationToken)
    {
        QueuedJob? job = null;
        lock (_lock)
        {
            for (var node = _failed.First; node is not null; node = node.Next)
            {
                if (node.Value.Job.Id == id)
                {
                    job = node.Value.Job;
                    _failed.Remove(node);
                    break;
                }
            }
        }

        if (job is null)
        {
            return false;
        }

        await queue.EnqueueAsync(job with { Attempt = 1 }, cancellationToken);
        return true;
    }
}
