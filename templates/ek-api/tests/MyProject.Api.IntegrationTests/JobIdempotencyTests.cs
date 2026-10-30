using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

public sealed class JobIdempotencyTests(ApiFactory api)
{
    [Fact]
    public async Task Running_an_import_again_has_no_effect_even_after_its_note_was_deleted()
    {
        var title = $"Once {Guid.NewGuid():N}";
#if (UseAuth)
        var ownerId = Guid.NewGuid().ToString();
        var import = new NoteImport(Guid.NewGuid(), ownerId, [new CreateNoteRequest { Title = title }]);
#else
        var import = new NoteImport(Guid.NewGuid(), [new CreateNoteRequest { Title = title }]);
#endif
        await RunImportAsync(import);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
            var created = Assert.Single(await FindAsync(store, title));
            Assert.Equal(NoteSaveResult.Saved, await store.TryDeleteAsync(created, TestContext.Current.CancellationToken));
        }

        // A second delivery of the same job, as after a crash or a retry.
        await RunImportAsync(import);

        await using var check = api.Services.CreateAsyncScope();
        Assert.Empty(await FindAsync(check.ServiceProvider.GetRequiredService<INoteStore>(), title));
#if (UseAuth)

        async Task<IReadOnlyList<Note>> FindAsync(INoteStore store, string search) =>
            (await store.ListAsync(ownerId, new NoteListQuery { Search = search }, TestContext.Current.CancellationToken)).Items;
#else

        static async Task<IReadOnlyList<Note>> FindAsync(INoteStore store, string search) =>
            (await store.ListAsync(new NoteListQuery { Search = search }, TestContext.Current.CancellationToken)).Items;
#endif
    }

    [Fact]
    public async Task A_run_that_throws_leaves_the_key_free_so_a_retry_runs_the_work()
    {
        var key = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunOnceAsync(key, _ => throw new InvalidOperationException("Broken on purpose.")));
        var retried = await RunOnceAsync(key, _ => Task.CompletedTask);
        var repeated = await RunOnceAsync(key, _ => Task.CompletedTask);

        Assert.True(retried);
        Assert.False(repeated);
    }

    private async Task RunImportAsync(NoteImport import)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ImportNotesJob>().RunAsync(import, TestContext.Current.CancellationToken);
    }

    private async Task<bool> RunOnceAsync(string key, Func<CancellationToken, Task> work)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobIdempotency>().RunOnceAsync("test", key, work, TestContext.Current.CancellationToken);
    }
}
