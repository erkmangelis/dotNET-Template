using System.Net.Http.Headers;
#if (!UseOidc)
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity.Data;
#endif

namespace MyProject.Api.IntegrationTests;

#if (UseOidc)
internal sealed record TestUser(string Id, string AccessToken, HttpClient Client);

internal static class TestUsers
{
    /// <summary>A new user at the provider: a fresh subject with a valid access token.</summary>
    public static Task<TestUser> SignInNewUserAsync(ApiFactory api)
    {
        var id = Guid.NewGuid().ToString();
        var token = TestTokens.Create(TestTokens.Issuer, TestTokens.Audience, new() { ["sub"] = id });
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return Task.FromResult(new TestUser(id, token, client));
    }
}
#else
internal sealed record TestUser(string Email, string Password, string AccessToken, HttpClient Client);

internal static class TestUsers
{
    public const string Password = "Correct-horse-battery-1";

    /// <summary>Registers a new user, confirms the email through the captured link and signs in.</summary>
    public static async Task<TestUser> SignInNewUserAsync(ApiFactory api)
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var client = api.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        var registered = await client.PostAsJsonAsync("/v1/auth/register", new RegisterRequest { Email = email, Password = Password }, cancellationToken);
        registered.EnsureSuccessStatusCode();

        var confirmed = await client.GetAsync(api.Emails.ConfirmationLinkFor(email), cancellationToken);
        confirmed.EnsureSuccessStatusCode();

        var token = await LoginAsync(client, email, Password, cancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        return new TestUser(email, Password, token.AccessToken, client);
    }

    public static async Task<AccessTokenResponse> LoginAsync(HttpClient client, string email, string password, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = email, Password = password }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken))!;
    }
}
#endif
