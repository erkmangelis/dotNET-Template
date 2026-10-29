using System.Net;
using System.Net.Http.Headers;

namespace MyProject.Api.IntegrationTests;

/// <summary>The API trusts only access tokens from the configured provider, for this API, that have not expired.</summary>
public sealed class OidcTests(ApiFactory api)
{
    [Fact]
    public async Task Notes_require_a_token()
    {
        using var anonymous = api.CreateClient();

        var response = await anonymous.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_access_token_is_accepted()
    {
        var user = await TestUsers.SignInNewUserAsync(api);

        var response = await user.Client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        user.Client.Dispose();
    }

    [Theory]
    [InlineData("another audience")]
    [InlineData("another issuer")]
    [InlineData("untrusted key")]
    [InlineData("expired")]
    public async Task Invalid_tokens_are_refused(string problem)
    {
        var claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() };
        var token = problem switch
        {
            "another audience" => TestTokens.Create(TestTokens.Issuer, "another-api", claims),
            "another issuer" => TestTokens.Create("https://evil.example.com", TestTokens.Audience, claims),
            "untrusted key" => TestTokens.Create(TestTokens.Issuer, TestTokens.Audience, claims, signingKey: TestTokens.UntrustedKey),
            _ => TestTokens.Create(TestTokens.Issuer, TestTokens.Audience, claims, expires: DateTime.UtcNow.AddMinutes(-10)),
        };
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
