using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class ApiVersionTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = api.CreateClient();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Both_versions_serve_the_same_notes_and_only_v1_counts_them()
    {
        var created = await _client.PostAsJsonAsync("/v2/notes", new { title = $"Note {Guid.NewGuid():N}" }, TestContext.Current.CancellationToken);
        var note = await created.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken);
        var query = $"?search={Uri.EscapeDataString(note!.Title)}";

        var v1 = await _client.GetFromJsonAsync<JsonElement>($"/v1/notes{query}", TestContext.Current.CancellationToken);
        var v2 = await _client.GetFromJsonAsync<JsonElement>($"/v2/notes{query}", TestContext.Current.CancellationToken);

        Assert.Equal($"/v2/notes/{note.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(1, v1.GetProperty("totalCount").GetInt64());
        Assert.False(v2.TryGetProperty("totalCount", out _));
        Assert.False(v2.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(
            v1.GetProperty("items")[0].GetProperty("id").GetGuid(),
            v2.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Responses_list_the_versions_an_endpoint_supports()
    {
        var response = await _client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal("1, 2", string.Join(", ", response.Headers.GetValues("api-supported-versions")));
    }

    [Theory]
    [InlineData("/v3/notes")]
    public async Task A_version_an_endpoint_does_not_serve_is_not_found(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Each_version_has_its_own_OpenAPI_document()
    {
        using var factory = api.WithWebHostBuilder(builder => builder.UseSetting("Api:ExposeOpenApi", "true"));
        using var client = factory.CreateClient();

        var v1 = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        var v2 = await client.GetFromJsonAsync<JsonElement>("/openapi/v2.json", TestContext.Current.CancellationToken);

        Assert.True(v1.GetProperty("paths").TryGetProperty("/v1/notes", out _));
        Assert.True(v2.GetProperty("paths").TryGetProperty("/v2/notes", out _));
        Assert.False(v2.GetProperty("paths").TryGetProperty("/v1/notes", out _));
    }
}
