using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class IdempotencyTests(ApiFactory api) : IAsyncLifetime
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
    public async Task A_retry_with_the_same_key_replays_the_first_response_and_creates_nothing()
    {
        var key = Guid.NewGuid().ToString();
        var title = UniqueTitle();

        var first = await PostAsync(key, new { title });
        var retry = await PostAsync(key, new { title });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues(IdempotencyMiddleware.ReplayedHeaderName).Single());
        Assert.False(first.Headers.Contains(IdempotencyMiddleware.ReplayedHeaderName));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var page = await _client.GetFromJsonAsync<PagedResult<NoteResponse>>($"/v1/notes?search={Uri.EscapeDataString(title)}", TestContext.Current.CancellationToken);
        Assert.Single(page!.Items);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_answers_422()
    {
        var key = Guid.NewGuid().ToString();
        await PostAsync(key, new { title = UniqueTitle() });

        var response = await PostAsync(key, new { title = UniqueTitle() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_retry_while_the_first_request_runs_answers_409()
    {
        var key = Guid.NewGuid().ToString();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var now = DateTimeOffset.UtcNow;
            await scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().TryReserveAsync(
                new IdempotencyRecord { Scope = await ScopeAsync(), Key = key, RequestHash = "running", CreatedAt = now, ExpiresAt = now.AddHours(1) },
                TestContext.Current.CancellationToken);
        }

        var response = await PostAsync(key, new { title = UniqueTitle() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("too-long")]
    public async Task An_invalid_key_answers_400(string key)
    {
        if (key == "too-long")
        {
            key = new string('k', IdempotencyRecord.KeyMaxLength + 1);
        }

        var response = await PostAsync(key, new { title = UniqueTitle() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<string> ScopeAsync() => Task.FromResult(IdempotencyMiddleware.AnonymousScope);

    private async Task<HttpResponseMessage> PostAsync(string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notes") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation(IdempotencyMiddleware.HeaderName, key);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string UniqueTitle() => $"Note {Guid.NewGuid():N}";
}
