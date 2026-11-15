using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>The test app gives jobs two attempts with no wait between them.</summary>
public sealed class JobFailureTests(ApiFactory api)
{
    [Fact]
    public async Task A_job_that_keeps_failing_is_listed_as_failed_after_its_attempts()
    {
        var (key, id) = await EnqueueBrokenJobAsync();

        var failed = await WaitForFailedAsync(id);

        Assert.Equal(2, FlakyJob.Runs[key]);
        Assert.Equal(nameof(FlakyJob), failed.Name);
        Assert.Contains("Broken on purpose.", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_fixed_a_requeued_job_runs_again_and_leaves_the_failed_list()
    {
        var (key, id) = await EnqueueBrokenJobAsync();
        await WaitForFailedAsync(id);
        FlakyJob.Broken.TryRemove(key, out _);

        Assert.True(await api.Services.GetRequiredService<IFailedJobs>().RequeueAsync(id, TestContext.Current.CancellationToken));

        await Eventually.HoldsAsync(() => Task.FromResult(FlakyJob.Runs[key]), runs => runs == 3);
        await Eventually.HoldsAsync(FailedAsync, failed => failed.All(job => job.Id != id));
    }

    private async Task<(string Key, string Id)> EnqueueBrokenJobAsync()
    {
        var key = Guid.NewGuid().ToString();
        FlakyJob.Broken[key] = true;
        var id = await api.Services.GetRequiredService<IJobQueue>().EnqueueAsync<FlakyJob, string>(key, TestContext.Current.CancellationToken);
        return (key, id);
    }

    private async Task<FailedJob> WaitForFailedAsync(string id) =>
        (await Eventually.HoldsAsync(FailedAsync, failed => failed.Any(job => job.Id == id))).Single(job => job.Id == id);

    private Task<IReadOnlyList<FailedJob>> FailedAsync() =>
        api.Services.GetRequiredService<IFailedJobs>().ListAsync(100, TestContext.Current.CancellationToken);
}
