using System.Net.Http.Headers;

namespace Acme.Notes.Api.IntegrationTests;

internal sealed record TestUser(string Id, string AccessToken, HttpClient Client);

internal static class TestUsers
{
    /// <summary>A new user at the provider: a fresh subject with a valid access token carrying <paramref name="roles"/>.</summary>
    public static Task<TestUser> SignInNewUserAsync(ApiFactory api, params string[] roles)
    {
        var id = Guid.NewGuid().ToString();
        var token = TestTokens.Create(TestTokens.Issuer, TestTokens.Audience, new() { ["sub"] = id, ["roles"] = roles });
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return Task.FromResult(new TestUser(id, token, client));
    }
}
