using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class JobTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = (await TestUsers.SignInNewUserAsync(api)).Client;

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_import_answers_202_and_a_background_job_creates_the_notes()
    {
        var prefix = $"Import {Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync(
            "/v1/notes/imports",
            new { notes = new[] { new { title = $"{prefix} one", content = (string?)null }, new { title = $"{prefix} two", content = (string?)"Second" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<ImportNotesResponse>(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(accepted!.JobId));
        var notes = await Eventually.HoldsAsync(() => SearchAsync(prefix), items => items.Count == 2);
        Assert.Contains(notes, note => note.Title == $"{prefix} two" && note.Content == "Second");
    }

    [Fact]
    public async Task An_import_with_an_invalid_note_answers_400()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/notes/imports",
            new { notes = new[] { new { title = "" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Purging_removes_only_notes_deleted_before_the_cutoff()
    {
        var kept = await CreateAsync();
        var deleted = await CreateAsync();
        (await _client.DeleteAsync($"/v1/notes/{deleted.Id}", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await using var scope = api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
        Assert.Equal(0, await store.PurgeDeletedAsync(DateTimeOffset.UtcNow.AddDays(-1), TestContext.Current.CancellationToken));
        var purged = await store.PurgeDeletedAsync(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);

        Assert.True(purged >= 1);
        Assert.NotNull(await store.FindAsync(kept.Id, TestContext.Current.CancellationToken));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Notes.IgnoreQueryFilters().AnyAsync(note => note.Id == deleted.Id, TestContext.Current.CancellationToken));
    }

    private async Task<NoteResponse> CreateAsync()
    {
        var response = await _client.PostAsJsonAsync("/v1/notes", new { title = $"Note {Guid.NewGuid():N}" }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken))!;
    }

    private async Task<IReadOnlyList<NoteResponse>> SearchAsync(string search) =>
        (await _client.GetFromJsonAsync<PagedResult<NoteResponse>>($"/v1/notes?search={Uri.EscapeDataString(search)}", TestContext.Current.CancellationToken))!.Items;
}
