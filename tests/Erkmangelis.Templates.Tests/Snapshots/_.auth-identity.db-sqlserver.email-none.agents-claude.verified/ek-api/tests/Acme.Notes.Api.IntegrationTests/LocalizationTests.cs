using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class LocalizationTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = (await TestUsers.SignInNewUserAsync(api)).Client;

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Validation_errors_are_in_the_language_the_caller_asks_for()
    {
        var response = await PostInvalidNoteAsync("tr-TR,tr;q=0.9,en;q=0.5");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // tr-TR falls back to tr; the header says which language the body is in.
        Assert.Equal("tr", Assert.Single(response.Content.Headers.ContentLanguage));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Bir veya daha fazla doğrulama hatası oluştu.", problem.GetProperty("title").GetString());
        Assert.Equal("Title alanı zorunludur.", problem.GetProperty("errors").GetProperty("Title")[0].GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("de-DE")]
    public async Task Callers_without_a_supported_language_get_english(string? acceptLanguage)
    {
        var response = await PostInvalidNoteAsync(acceptLanguage);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("One or more validation errors occurred.", problem.GetProperty("title").GetString());
        Assert.Equal("The Title field is required.", problem.GetProperty("errors").GetProperty("Title")[0].GetString());
    }

    [Fact]
    public async Task Problems_the_framework_writes_are_translated_too()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/notes/{Guid.NewGuid()}");
        request.Headers.AcceptLanguage.ParseAdd("tr");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Bulunamadı", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Turkish_callers_still_search_without_the_turkish_i_rules()
    {
        // Only messages change language. With a Turkish culture, "i" and "I" would not match ignoring case.
        var title = $"Invoice {Guid.NewGuid():N}";
        (await _client.PostAsJsonAsync("/v1/notes", new { title }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/notes?search={Uri.EscapeDataString(title.ToLowerInvariant())}");
        request.Headers.AcceptLanguage.ParseAdd("tr");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(title, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
    }

    private async Task<HttpResponseMessage> PostInvalidNoteAsync(string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notes") { Content = JsonContent.Create(new { title = "" }) };
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
