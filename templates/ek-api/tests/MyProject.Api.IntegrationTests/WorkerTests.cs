extern alias worker;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

public sealed class WorkerTests(ApiFactory api)
{
    [Fact]
    public async Task The_worker_runs_the_jobs_the_api_queues()
    {
        // A queue of its own, so the shared test app's server leaves this job alone.
        var queue = $"worker_{Guid.NewGuid():N}";
        var title = $"Worker {Guid.NewGuid():N}";
#if (UseAuth)
        var ownerId = Guid.NewGuid().ToString();
        var import = new NoteImport(Guid.NewGuid(), ownerId, [new CreateNoteRequest { Title = title }]);
#else
        var import = new NoteImport(Guid.NewGuid(), [new CreateNoteRequest { Title = title }]);
#endif

        // The API queues the job and runs no server, as it does when deployed with the worker.
        await using var queuing = api.WithWebHostBuilder(builder => builder.UseSetting("Jobs:Queue", queue));
        await queuing.Services.GetRequiredService<IJobQueue>()
            .EnqueueAsync<ImportNotesJob, NoteImport>(import, TestContext.Current.CancellationToken);

        await using var workerApp = new WorkerFactory(api.ConnectionString, queue);
        using var client = workerApp.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);

        await Eventually.HoldsAsync(
            async () =>
            {
                await using var scope = api.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
#if (UseAuth)
                return (await store.ListAsync(ownerId, new NoteListQuery { Search = title }, TestContext.Current.CancellationToken)).Items;
#else
                return (await store.ListAsync(new NoteListQuery { Search = title }, TestContext.Current.CancellationToken)).Items;
#endif
            },
            notes => notes.Count == 1);
    }

    private sealed class WorkerFactory(string connectionString, string queue) : WebApplicationFactory<worker::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Jobs:Queue", queue);
            builder.UseSetting("Jobs:PollIntervalSeconds", "1");
            // Hangfire keeps the retry filter in a static, so the worker's settings would replace the
            // test API's for every job in this process; keep them the same.
            builder.UseSetting("Jobs:MaxAttempts", "2");
            builder.UseSetting("Jobs:RetryDelaySeconds", "0");
#if (UseSmtp)
            builder.UseSetting("Email:Transport", "Log");
            builder.UseSetting("Email:FromAddress", "noreply@example.com");
#endif
            builder.ConfigureTestServices(ApiFactory.WithoutHangfireLogs);
        }
    }
}
