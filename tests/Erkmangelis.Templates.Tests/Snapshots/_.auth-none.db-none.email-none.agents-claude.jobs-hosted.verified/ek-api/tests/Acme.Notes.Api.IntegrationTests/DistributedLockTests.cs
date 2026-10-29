using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class DistributedLockTests(ApiFactory api)
{
    private readonly IDistributedLock _locks = api.Services.GetRequiredService<IDistributedLock>();

    [Fact]
    public async Task Only_one_holder_at_a_time_and_released_on_dispose()
    {
        var name = $"test:{Guid.NewGuid():N}";

        var first = await _locks.TryAcquireAsync(name, TestContext.Current.CancellationToken);
        var second = await _locks.TryAcquireAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Null(second);

        await first.DisposeAsync();
        await using var third = await _locks.TryAcquireAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(third);
    }

    [Fact]
    public async Task Locks_with_different_names_do_not_block_each_other()
    {
        await using var first = await _locks.TryAcquireAsync($"test:{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await using var second = await _locks.TryAcquireAsync($"test:{Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task A_recurring_job_runs_once_per_scheduled_time_however_many_instances_schedule_it()
    {
        var job = new RecurringJobDefinition($"count-{Guid.NewGuid():N}", typeof(CountingRecurringJob), "* * * * *");
        var scheduledAt = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        var before = CountingRecurringJob.Runs;

        // Two instances at the same time, then one that comes late.
        await Task.WhenAll(RunAsync(job, scheduledAt), RunAsync(job, scheduledAt));
        await RunAsync(job, scheduledAt);
        Assert.Equal(before + 1, CountingRecurringJob.Runs);

        await RunAsync(job, scheduledAt.AddMinutes(1));
        Assert.Equal(before + 2, CountingRecurringJob.Runs);
    }

    private async Task RunAsync(RecurringJobDefinition job, DateTimeOffset scheduledAt)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RecurringJobRunner>().RunAsync(job, scheduledAt, TestContext.Current.CancellationToken);
    }
}
