using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Acme.Notes.Api.UnitTests.Infrastructure.Jobs;

public sealed class ScopedBackgroundLoopTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new();
    private readonly ServiceProvider _services = new ServiceCollection().AddScoped<ScopeProbe>().BuildServiceProvider();
    private TestLoop _loop = null!;

    public ValueTask InitializeAsync()
    {
        _loop = new TestLoop(_services.GetRequiredService<IServiceScopeFactory>(), _time);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.StopAsync(CancellationToken.None);
        _loop.Dispose();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Runs_at_startup_and_after_every_interval_each_time_in_its_own_scope()
    {
        await _loop.StartAsync(TestContext.Current.CancellationToken);
        var first = await NextRunAsync();

        _time.Advance(TestLoop.Every);
        var second = await NextRunAsync();

        Assert.NotSame(first, second);
        Assert.True(first.Disposed, "the first run's scope was not disposed");
    }

    [Fact]
    public async Task A_failed_run_does_not_stop_the_loop()
    {
        _loop.FailNextRun = true;
        await _loop.StartAsync(TestContext.Current.CancellationToken);
        await NextRunAsync();

        _time.Advance(TestLoop.Every);

        // Times out if the failure ended the loop.
        await NextRunAsync();
    }

    [Fact]
    public async Task Stopping_the_app_cancels_the_run_in_progress()
    {
        _loop.WaitForCancellation = true;
        await _loop.StartAsync(TestContext.Current.CancellationToken);
        await NextRunAsync();

        await _loop.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(_loop.WasCancelled);
    }

    private async Task<ScopeProbe> NextRunAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _loop.Runs.Reader.ReadAsync(timeout.Token);
    }

    private sealed class ScopeProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class TestLoop(IServiceScopeFactory scopes, TimeProvider time)
        : ScopedBackgroundLoop(scopes, time, NullLogger.Instance)
    {
        public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

        public Channel<ScopeProbe> Runs { get; } = Channel.CreateUnbounded<ScopeProbe>();

        public bool FailNextRun { get; set; }

        public bool WaitForCancellation { get; set; }

        public bool WasCancelled { get; private set; }

        protected override string Name => "test";

        protected override TimeSpan Interval => Every;

        protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            Runs.Writer.TryWrite(services.GetRequiredService<ScopeProbe>());
            if (FailNextRun)
            {
                FailNextRun = false;
                throw new InvalidOperationException("The run failed.");
            }

            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    throw;
                }
            }
        }
    }
}
