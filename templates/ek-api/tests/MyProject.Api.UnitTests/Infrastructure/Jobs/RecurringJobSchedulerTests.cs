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
    public async Task Queues_a_job_at_the_time_its_schedule_gives_and_not_before()
    {
        var time = new WatchedTime(new DateTimeOffset(2026, 1, 1, 2, 59, 30, TimeSpan.Zero));
        var options = Options.Create(new JobsOptions());
        var queue = new InProcessJobQueue(options);
        await using var services = new ServiceCollection()
            .AddRecurringJob<CountingJob>("counting", "0 3 * * *")
            .BuildServiceProvider();
        using var scheduler = new RecurringJobScheduler(
            services.GetServices<RecurringJobDefinition>(), queue, time, options, NullLogger<RecurringJobScheduler>.Instance);

        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        // The scheduler starts on a thread of its own: wait until it waits for 03:00.
        await time.Timers.Reader.ReadAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(queue.Reader.TryRead(out _));

        time.Advance(TimeSpan.FromSeconds(1));
        var due = await NextAsync(queue.Reader);

        Assert.Equal("counting", due.Name);
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Configuration_can_replace_a_default_schedule()
    {
        var options = new JobsOptions { Schedules = { ["counting"] = "*/5 * * * *" } };

        Assert.Equal("*/5 * * * *", options.ScheduleFor(new RecurringJobDefinition("counting", typeof(CountingJob), "0 3 * * *")));
        Assert.Equal("0 3 * * *", options.ScheduleFor(new RecurringJobDefinition("other", typeof(CountingJob), "0 3 * * *")));
    }

    private static async Task<T> NextAsync<T>(ChannelReader<T> reader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await reader.ReadAsync(timeout.Token);
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

    private sealed class CountingJob : IRecurringJob
    {
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
