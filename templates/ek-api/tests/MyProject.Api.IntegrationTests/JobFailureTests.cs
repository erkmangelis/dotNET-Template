#if (UseAuth)
using System.Net;
using System.Net.Http.Json;
#endif
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

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

#if (UseAuth)
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);
        var response = await admin.Client.PostAsync($"/v1/admin/jobs/failed/{id}/requeue", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
#else
        Assert.True(await api.Services.GetRequiredService<IFailedJobs>().RequeueAsync(id, TestContext.Current.CancellationToken));
#endif

        await Eventually.HoldsAsync(() => Task.FromResult(FlakyJob.Runs[key]), runs => runs == 3);
        await Eventually.HoldsAsync(FailedAsync, failed => failed.All(job => job.Id != id));
    }
#if (UseAuth)

    [Fact]
    public async Task Admins_see_failed_jobs_and_other_users_get_403()
    {
        var (_, id) = await EnqueueBrokenJobAsync();
        await WaitForFailedAsync(id);
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);
        var user = await TestUsers.SignInNewUserAsync(api);

        var failed = await admin.Client.GetFromJsonAsync<List<FailedJob>>("/v1/admin/jobs/failed", TestContext.Current.CancellationToken);
        var forbidden = await user.Client.GetAsync("/v1/admin/jobs/failed", TestContext.Current.CancellationToken);
        var requeue = await user.Client.PostAsync($"/v1/admin/jobs/failed/{id}/requeue", content: null, TestContext.Current.CancellationToken);

        Assert.Contains(failed!, job => job.Id == id);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, requeue.StatusCode);
    }

    [Fact]
    public async Task Requeueing_an_unknown_job_answers_404()
    {
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);

        var response = await admin.Client.PostAsync("/v1/admin/jobs/failed/no-such-job/requeue", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endif

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
