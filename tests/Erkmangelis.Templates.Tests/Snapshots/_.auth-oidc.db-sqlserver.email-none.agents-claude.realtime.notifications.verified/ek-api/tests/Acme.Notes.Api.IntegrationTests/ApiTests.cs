using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class ApiTests(ApiFactory api)
{
    private readonly HttpClient _client = api.CreateClient();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_answer_200(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Unknown_routes_answer_problem_details_with_a_trace_id()
    {
        var response = await _client.GetAsync("/v1/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task OpenApi_is_hidden_unless_turned_on()
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_describes_the_v1_routes_when_turned_on()
    {
        using var factory = api.WithWebHostBuilder(builder => builder.UseSetting("Api:ExposeOpenApi", "true"));
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.True(document.GetProperty("paths").TryGetProperty("/v1/notes", out _));
    }

    [Fact]
    public async Task Callers_over_the_rate_limit_get_429_with_problem_details()
    {
        using var factory = api.WithWebHostBuilder(builder => builder
            .UseSetting("RateLimiting:Enabled", "true")
            .UseSetting("RateLimiting:PermitLimit", "2")
            .UseSetting("RateLimiting:WindowSeconds", "60"));
        using var client = factory.CreateClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await client.GetAsync("/v1/does-not-exist", TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("application/problem+json", last.Content.Headers.ContentType?.MediaType);
    }
}
