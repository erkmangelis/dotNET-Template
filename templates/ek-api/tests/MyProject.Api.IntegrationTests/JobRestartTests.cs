using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

public sealed class JobRestartTests(ApiFactory api)
{
    [Fact]
    public async Task A_queued_job_survives_a_restart_and_runs_afterwards()
    {
        // A queue of its own, so the shared test app's server leaves this job alone.
        var queue = $"restart_{Guid.NewGuid():N}";
        var title = $"Restart {Guid.NewGuid():N}";
#if (UseAuth)
        var ownerId = Guid.NewGuid().ToString();
        var import = new NoteImport(Guid.NewGuid(), ownerId, [new CreateNoteRequest { Title = title }]);
#else
        var import = new NoteImport(Guid.NewGuid(), [new CreateNoteRequest { Title = title }]);
#endif

        // An app that queues jobs without running them, then stops.
        string jobId;
        await using (var before = Restarted(queue, runServer: false))
        {
            jobId = await before.Services.GetRequiredService<IJobQueue>()
                .EnqueueAsync<ImportNotesJob, NoteImport>(import, TestContext.Current.CancellationToken);
        }

        // The app starts again, this time with a server for that queue.
        await using var after = Restarted(queue, runServer: true);
        var monitoring = after.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        await Eventually.HoldsAsync(() => Task.FromResult(monitoring.JobDetails(jobId).History[0].StateName), state => state == "Succeeded");

        await using var scope = after.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
#if (UseAuth)
        var notes = await store.ListAsync(ownerId, new NoteListQuery { Search = title }, TestContext.Current.CancellationToken);
#else
        var notes = await store.ListAsync(new NoteListQuery { Search = title }, TestContext.Current.CancellationToken);
#endif
        Assert.Single(notes.Items);
    }

    private WebApplicationFactory<Program> Restarted(string queue, bool runServer) =>
        api.WithWebHostBuilder(builder => builder
            .UseSetting("Jobs:Queue", queue)
            .UseSetting("Jobs:RunServer", runServer ? "true" : "false"));
}
