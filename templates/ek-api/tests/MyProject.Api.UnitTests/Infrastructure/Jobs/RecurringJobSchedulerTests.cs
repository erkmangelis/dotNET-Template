using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MyProject.Api.UnitTests.Infrastructure.Jobs;

public sealed class RecurringJobSchedulerTests
{
    [Theory]
    [InlineData("0 3 * * *", true)]
    [InlineData("*/5 * * * *", true)]
    [InlineData("every day", false)]
    [InlineData("0 0 3 * * *", false)]
    public void Schedules_are_five_field_cron_expressions(string cron, bool valid) =>
        Assert.Equal(valid, RecurringJobScheduler.IsValidCron(cron));

    [Fact]
    public async Task Runs_a_job_at_the_time_its_schedule_gives_and_not_before()
    {
        var time = new WatchedTime(new DateTimeOffset(2026, 1, 1, 2, 59, 30, TimeSpan.Zero));
        var runs = Channel.CreateUnbounded<DateTimeOffset>();
        await using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(runs)
            .AddRecurringJob<CountingJob>("counting", "0 3 * * *")
            .BuildServiceProvider();
        using var scheduler = new RecurringJobScheduler(
            services.GetServices<RecurringJobDefinition>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new JobsOptions()),
            NullLogger<RecurringJobScheduler>.Instance);

        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        // The scheduler starts on a thread of its own: wait until it waits for 03:00.
        await time.Timers.Reader.ReadAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(runs.Reader.TryRead(out _));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero), await NextRunAsync(runs));
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Configuration_can_replace_a_default_schedule()
    {
        var options = new JobsOptions { Schedules = { ["counting"] = "*/5 * * * *" } };

        Assert.Equal("*/5 * * * *", options.ScheduleFor(new RecurringJobDefinition("counting", typeof(CountingJob), "0 3 * * *")));
        Assert.Equal("0 3 * * *", options.ScheduleFor(new RecurringJobDefinition("other", typeof(CountingJob), "0 3 * * *")));
    }

    private static async Task<DateTimeOffset> NextRunAsync(Channel<DateTimeOffset> runs)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await runs.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Fake time that reports each timer the scheduler sets, such as the one behind a delay.</summary>
    private sealed class WatchedTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public Channel<TimeSpan> Timers { get; } = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Timers.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class CountingJob(Channel<DateTimeOffset> runs, TimeProvider time) : IRecurringJob
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            runs.Writer.TryWrite(time.GetUtcNow());
            return Task.CompletedTask;
        }
    }
}
