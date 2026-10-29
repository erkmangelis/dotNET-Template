using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity.Data;

namespace MyProject.Api.IntegrationTests;

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
