using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests.Caching;

public sealed class CachingTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = (await TestUsers.SignInNewUserAsync(api)).Client;

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_read_note_is_served_from_the_cache()
    {
        var note = await CreateAsync();
        await _client.GetFromJsonAsync<NoteResponse>($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);

        // Changed behind the endpoints' back, so nothing removes the cached copy.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
            var stored = await store.FindAsync(note.Id, TestContext.Current.CancellationToken);
            stored!.Title = $"Changed {Guid.NewGuid():N}";
            Assert.Equal(NoteSaveResult.Saved, await store.TryUpdateAsync(stored, TestContext.Current.CancellationToken));
        }

        var read = await _client.GetFromJsonAsync<NoteResponse>($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(note.Title, read!.Title);
    }

    [Fact]
    public async Task An_update_through_the_api_is_read_back_at_once()
    {
        var note = await CreateAsync();
        await _client.GetFromJsonAsync<NoteResponse>($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        var title = $"Updated {Guid.NewGuid():N}";

        var updated = await _client.PutAsJsonAsync($"/v1/notes/{note.Id}", new { title, version = note.Version }, TestContext.Current.CancellationToken);
        updated.EnsureSuccessStatusCode();

        var read = await _client.GetFromJsonAsync<NoteResponse>($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(title, read!.Title);
    }

    [Fact]
    public async Task A_deleted_note_is_not_served_from_the_cache()
    {
        var note = await CreateAsync();
        await _client.GetFromJsonAsync<NoteResponse>($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);

        (await _client.DeleteAsync($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_cached_note_stays_hidden_from_other_users()
    {
        var note = await CreateAsync();
        await _client.GetFromJsonAsync<NoteResponse>($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        using var other = (await TestUsers.SignInNewUserAsync(api)).Client;

        var response = await other.GetAsync($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_is_output_cached()
    {
        await using var factory = api.WithWebHostBuilder(builder => builder.UseSetting("Api:ExposeOpenApi", "true"));
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        first.EnsureSuccessStatusCode();
        Assert.Null(first.Headers.Age);
        // Age is set only on a response served from the output cache.
        Assert.NotNull(second.Headers.Age);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<NoteResponse> CreateAsync()
    {
        var created = await _client.PostAsJsonAsync("/v1/notes", new { title = $"Cached {Guid.NewGuid():N}" }, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken))!;
    }
}
