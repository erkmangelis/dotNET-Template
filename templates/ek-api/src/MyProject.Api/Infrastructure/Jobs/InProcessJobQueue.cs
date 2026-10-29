using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Infrastructure.Jobs;

/// <summary>One queued job: what to run, resolved from the scope it runs in.</summary>
internal sealed record QueuedJob(string Id, string Name, Func<IServiceProvider, CancellationToken, Task> RunAsync);

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
        await _jobs.Writer.WriteAsync(job, cancellationToken);
        return job.Id;
    }
}

/// <summary>Runs queued jobs, <see cref="JobsOptions.WorkerCount"/> at a time, each in its own scope.</summary>
internal sealed partial class InProcessJobRunner(
    InProcessJobQueue queue,
    IServiceScopeFactory scopes,
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
            LogSucceeded(logger, job.Name, job.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogAbandoned(logger, job.Name, job.Id);
        }
        catch (Exception exception)
        {
            LogFailed(logger, job.Name, job.Id, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Name} {JobId} succeeded")]
    private static partial void LogSucceeded(ILogger logger, string name, string jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Name} {JobId} was stopped by shutdown and is lost")]
    private static partial void LogAbandoned(ILogger logger, string name, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Name} {JobId} failed")]
    private static partial void LogFailed(ILogger logger, string name, string jobId, Exception exception);
}
