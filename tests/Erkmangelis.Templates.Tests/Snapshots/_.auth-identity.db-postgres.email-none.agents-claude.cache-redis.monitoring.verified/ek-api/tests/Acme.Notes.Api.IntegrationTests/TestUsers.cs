using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests;

internal sealed record TestUser(string Email, string Password, string AccessToken, HttpClient Client);

internal static class TestUsers
{
    public const string Password = "Correct-horse-battery-1";

    /// <summary>Registers a new user, confirms the email through the captured link, adds <paramref name="roles"/> and signs in.</summary>
    public static async Task<TestUser> SignInNewUserAsync(ApiFactory api, params string[] roles)
    {
        var client = api.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = await RegisterConfirmedUserAsync(api, client);
        if (roles.Length > 0)
        {
            await using var scope = api.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.AddToRolesAsync((await users.FindByEmailAsync(email))!, roles);
        }

        var token = await LoginAsync(client, email, Password, cancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        return new TestUser(email, Password, token.AccessToken, client);
    }

    /// <summary>Registers a new user with <see cref="Password"/> and confirms the email. Returns the email.</summary>
    public static async Task<string> RegisterConfirmedUserAsync(ApiFactory api, HttpClient client)
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var cancellationToken = TestContext.Current.CancellationToken;

        var registered = await client.PostAsJsonAsync("/v1/auth/register", new RegisterRequest { Email = email, Password = Password }, cancellationToken);
        registered.EnsureSuccessStatusCode();

        var confirmed = await client.GetAsync(api.Emails.ConfirmationLinkFor(email), cancellationToken);
        confirmed.EnsureSuccessStatusCode();

        return email;
    }

    public static async Task<AccessTokenResponse> LoginAsync(HttpClient client, string email, string password, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = email, Password = password }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken))!;
    }
}
