namespace Acme.Notes.Api.UnitTests.Features.Notes;

public sealed class InMemoryNoteStoreTests
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_second_note_with_the_same_title_is_rejected()
    {
        var store = new InMemoryNoteStore(TimeProvider.System);

        Assert.True(await store.TryAddAsync(NewNote("Groceries", _start), TestContext.Current.CancellationToken));
        Assert.False(await store.TryAddAsync(NewNote("Groceries", _start.AddMinutes(1)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_returns_newest_first_one_page_at_a_time()
    {
        var store = new InMemoryNoteStore(TimeProvider.System);
        for (var i = 0; i < 5; i++)
        {
            await store.TryAddAsync(NewNote($"Note {i}", _start.AddMinutes(i)), TestContext.Current.CancellationToken);
        }

        var page = await store.ListAsync(new NoteListQuery { Page = 2, PageSize = 2 }, TestContext.Current.CancellationToken);

        Assert.Equal(["Note 2", "Note 1"], page.Items.Select(note => note.Title));
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
    }

    [Fact]
    public async Task List_filters_by_title_and_time_then_sorts()
    {
        var store = new InMemoryNoteStore(TimeProvider.System);
        await store.TryAddAsync(NewNote("Buy milk", _start), TestContext.Current.CancellationToken);
        await store.TryAddAsync(NewNote("Buy bread", _start.AddMinutes(1)), TestContext.Current.CancellationToken);
        await store.TryAddAsync(NewNote("Call mum", _start.AddMinutes(2)), TestContext.Current.CancellationToken);
        await store.TryAddAsync(NewNote("buy eggs", _start.AddMinutes(3)), TestContext.Current.CancellationToken);

        var page = await store.ListAsync(
            new NoteListQuery { Search = "BUY", CreatedFrom = _start.AddMinutes(1), Sort = "title" },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Buy bread", "buy eggs"], page.Items.Select(note => note.Title));
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task A_change_based_on_an_old_version_is_refused()
    {
        var store = new InMemoryNoteStore(TimeProvider.System);
        var note = NewNote("Groceries", _start);
        await store.TryAddAsync(note, TestContext.Current.CancellationToken);
        var first = (await store.FindAsync(note.Id, TestContext.Current.CancellationToken))!;
        var second = (await store.FindAsync(note.Id, TestContext.Current.CancellationToken))!;

        first.Content = "Milk";
        second.Content = "Eggs";

        Assert.Equal(NoteSaveResult.Saved, await store.TryUpdateAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(NoteSaveResult.Stale, await store.TryUpdateAsync(second, TestContext.Current.CancellationToken));
        Assert.Equal(NoteSaveResult.Stale, await store.TryDeleteAsync(second, TestContext.Current.CancellationToken));
        Assert.Equal("Milk", (await store.FindAsync(note.Id, TestContext.Current.CancellationToken))!.Content);
    }

    private static Note NewNote(string title, DateTimeOffset createdAt) =>
        new() { Id = Guid.CreateVersion7(createdAt), Title = title, CreatedAt = createdAt };
}
