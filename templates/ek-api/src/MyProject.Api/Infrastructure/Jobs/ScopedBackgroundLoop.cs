namespace MyProject.Api.Infrastructure.Jobs;

/// <summary>
/// The hosted service pattern: work that repeats inside the app on a fixed interval. It runs once
/// at startup and then every <see cref="Interval"/>. Each run gets its own dependency injection scope,
/// so it can use scoped services such as the database context. A failed run is logged and the loop
/// carries on; shutting the app down cancels the run in progress and ends the loop.
/// </summary>
/// <remarks>
/// Every instance of the app runs its own loop, so keep the work safe to repeat (deleting expired rows,
/// for example). Work that must run once across instances, survive restarts or retry belongs in a job.
/// </remarks>
internal abstract partial class ScopedBackgroundLoop(IServiceScopeFactory scopes, TimeProvider time, ILogger logger) : BackgroundService
{
    /// <summary>A short name for the logs.</summary>
    protected abstract string Name { get; }

    protected abstract TimeSpan Interval { get; }

    /// <summary>One run. <paramref name="services"/> belongs to a scope that ends with the run.</summary>
    protected abstract Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailed(logger, Name, Interval, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background loop {Name} failed; it runs again in {Interval}")]
    private static partial void LogFailed(ILogger logger, string name, TimeSpan interval, Exception exception);
}
