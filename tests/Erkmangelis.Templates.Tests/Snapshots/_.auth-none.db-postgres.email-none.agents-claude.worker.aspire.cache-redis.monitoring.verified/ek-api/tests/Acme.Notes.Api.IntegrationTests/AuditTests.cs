using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class AuditTests(ApiFactory api) : IDisposable
{
    private HttpClient Client { get; } = api.CreateClient();

    public void Dispose() => Client.Dispose();

    [Fact]
    public async Task A_notes_history_records_each_change_with_its_values_before_and_after()
    {
        var note = await CreateAsync();
        var title = $"Renamed {Guid.NewGuid():N}";
        await Client.PutAsJsonAsync($"/v1/notes/{note.Id}", new { title, content = note.Content }, TestContext.Current.CancellationToken);
        // Saving the same values again changes nothing, so it is not recorded.
        await Client.PutAsJsonAsync($"/v1/notes/{note.Id}", new { title, content = note.Content }, TestContext.Current.CancellationToken);

        var history = await Client.GetFromJsonAsync<PagedResult<AuditEntryResponse>>($"/v1/notes/{note.Id}/history", TestContext.Current.CancellationToken);

        Assert.NotNull(history);
        Assert.Equal(["Updated", "Created"], history.Items.Select(entry => entry.Action));
        var renamed = history.Items[0].Changes.GetProperty("Title");
        Assert.Equal((note.Title, title), (renamed.GetProperty("from").GetString(), renamed.GetProperty("to").GetString()));
        Assert.False(history.Items[0].Changes.TryGetProperty("Content", out _));
        Assert.Equal(note.Title, history.Items[1].Changes.GetProperty("Title").GetProperty("to").GetString());
    }

    [Fact]
    public async Task A_deleted_note_stays_in_the_database_but_no_query_returns_it()
    {
        var note = await CreateAsync();

        await Client.DeleteAsync($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);

        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Notes.AnyAsync(row => row.Id == note.Id, TestContext.Current.CancellationToken));
        var stored = await db.Notes.IgnoreQueryFilters([SoftDelete.Filter])
            .SingleAsync(row => row.Id == note.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(stored.DeletedAt);
        var actions = await db.AuditEntries
            .Where(entry => entry.EntityType == nameof(Note) && entry.EntityId == note.Id.ToString())
            .Select(entry => entry.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([AuditAction.Created, AuditAction.Deleted], actions.Order());
        var history = await Client.GetAsync($"/v1/notes/{note.Id}/history", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
    }

    private async Task<NoteResponse> CreateAsync()
    {
        var response = await Client.PostAsJsonAsync("/v1/notes", new { title = $"Note {Guid.NewGuid():N}", content = "Milk" }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken))!;
    }
}
