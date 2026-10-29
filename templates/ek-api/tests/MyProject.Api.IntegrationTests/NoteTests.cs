using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyProject.Api.IntegrationTests;

public sealed class NoteTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _client = null!;

#if (UseAuth)
    public async ValueTask InitializeAsync() => _client = (await TestUsers.SignInNewUserAsync(api)).Client;
#else
    public ValueTask InitializeAsync()
    {
        _client = api.CreateClient();
        return ValueTask.CompletedTask;
    }
#endif

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_created_note_can_be_read_back()
    {
        var title = UniqueTitle();

        var created = await _client.PostAsJsonAsync("/v1/notes", new { title, content = "Milk" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = await created.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal($"/v1/notes/{note.Id}", created.Headers.Location?.OriginalString);

        var fetched = await _client.GetFromJsonAsync<NoteResponse>(created.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(note, fetched);
    }

    [Fact]
    public async Task Invalid_input_answers_400_with_the_failing_fields()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/notes",
            new { title = "", content = new string('x', Note.ContentMaxLength + 1) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Title", out _));
        Assert.True(errors.TryGetProperty("Content", out _));
    }

    [Fact]
    public async Task A_duplicate_title_answers_409()
    {
        var title = UniqueTitle();
        await _client.PostAsJsonAsync("/v1/notes", new { title }, TestContext.Current.CancellationToken);

        var response = await _client.PostAsJsonAsync("/v1/notes", new { title }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_missing_note_answers_404()
    {
        var response = await _client.GetAsync($"/v1/notes/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Fields_the_server_owns_cannot_be_set_by_the_client()
    {
        var forgedId = Guid.NewGuid();
        var forgedDate = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var response = await _client.PostAsJsonAsync(
            "/v1/notes",
#if (UseAuth)
            new { id = forgedId, title = UniqueTitle(), createdAt = forgedDate, ownerId = "someone-else" },
#else
            new { id = forgedId, title = UniqueTitle(), createdAt = forgedDate },
#endif
            TestContext.Current.CancellationToken);

        var note = await response.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.NotEqual(forgedId, note.Id);
        Assert.NotEqual(forgedDate, note.CreatedAt);
        var fetched = await _client.GetAsync($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task The_list_is_paged_newest_first()
    {
        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync("/v1/notes", new { title = UniqueTitle() }, TestContext.Current.CancellationToken);
        }

        var page = await _client.GetFromJsonAsync<PagedResult<NoteResponse>>("/v1/notes?page=1&pageSize=2", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.TotalCount >= 3);
        Assert.True(page.HasNextPage);
        Assert.True(page.Items[0].CreatedAt >= page.Items[1].CreatedAt);
    }

    [Theory]
    [InlineData("/v1/notes?page=0")]
    [InlineData("/v1/notes?pageSize=101")]
    public async Task Out_of_range_paging_answers_400(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string UniqueTitle() => $"Note {Guid.NewGuid():N}";
}
