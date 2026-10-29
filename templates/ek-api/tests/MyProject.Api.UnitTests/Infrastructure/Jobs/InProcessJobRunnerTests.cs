using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyProject.Api.UnitTests.Infrastructure.Jobs;

public sealed class InProcessJobRunnerTests : IAsyncLifetime
{
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly InProcessJobQueue _queue;
    private readonly InProcessFailedJobs _failed;
    private readonly InProcessJobRunner _runner;
    private int _runs;

    public InProcessJobRunnerTests()
    {
        var options = Options.Create(new JobsOptions { MaxAttempts = 3, RetryDelaySeconds = 0, WorkerCount = 1 });
        _queue = new InProcessJobQueue(options);
        _failed = new InProcessFailedJobs(_queue);
        _runner = new InProcessJobRunner(
            _queue,
            _failed,
            _services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            options,
            NullLogger<InProcessJobRunner>.Instance);
    }

    public async ValueTask InitializeAsync() => await _runner.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _runner.StopAsync(CancellationToken.None);
        _runner.Dispose();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task A_job_that_fails_is_retried_until_it_succeeds()
    {
        await _queue.EnqueueAsync(Job(failTimes: 2), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => _runs == 3);

        Assert.Empty(await _failed.ListAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_job_that_keeps_failing_stops_after_the_last_attempt_and_is_listed_as_failed()
    {
        await _queue.EnqueueAsync(Job(failTimes: int.MaxValue), TestContext.Current.CancellationToken);

        var failed = await WaitForFailedAsync();

        Assert.Equal(3, _runs);
        Assert.Equal(3, failed.Attempts);
        Assert.Equal("InvalidOperationException: Broken on purpose.", failed.Error);
    }

    [Fact]
    public async Task A_requeued_job_runs_again_with_all_its_attempts()
    {
        await _queue.EnqueueAsync(Job(failTimes: 3), TestContext.Current.CancellationToken);
        var failed = await WaitForFailedAsync();

        Assert.True(await _failed.RequeueAsync(failed.Id, TestContext.Current.CancellationToken));

        await WaitUntilAsync(() => _runs == 4);
        Assert.Empty(await _failed.ListAsync(10, TestContext.Current.CancellationToken));
        Assert.False(await _failed.RequeueAsync(failed.Id, TestContext.Current.CancellationToken));
    }

    private QueuedJob Job(int failTimes) => new("job-1", "test", (_, _) =>
        Interlocked.Increment(ref _runs) <= failTimes
            ? throw new InvalidOperationException("Broken on purpose.")
            : Task.CompletedTask);

    private async Task<FailedJob> WaitForFailedAsync()
    {
        IReadOnlyList<FailedJob> failed = [];
        await WaitUntilAsync(async () => (failed = await _failed.ListAsync(10, CancellationToken.None)).Count == 1);
        return failed[0];
    }

    private static Task WaitUntilAsync(Func<bool> done) => WaitUntilAsync(() => Task.FromResult(done()));

    private static async Task WaitUntilAsync(Func<Task<bool>> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await done())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for the runner.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
