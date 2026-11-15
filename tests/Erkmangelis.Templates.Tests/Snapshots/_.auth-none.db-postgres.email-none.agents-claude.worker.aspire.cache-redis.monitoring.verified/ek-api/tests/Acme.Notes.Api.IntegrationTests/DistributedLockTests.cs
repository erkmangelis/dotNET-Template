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
}
