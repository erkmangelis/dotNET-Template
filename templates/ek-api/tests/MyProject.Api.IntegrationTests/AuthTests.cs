using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity.Data;

namespace MyProject.Api.IntegrationTests;

public sealed class AuthTests(ApiFactory api)
{
    [Fact]
    public async Task Notes_require_a_signed_in_user()
    {
        using var anonymous = api.CreateClient();

        var response = await anonymous.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unconfirmed_email_cannot_sign_in()
    {
        using var client = api.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/v1/auth/register", new RegisterRequest { Email = email, Password = TestUsers.Password }, TestContext.Current.CancellationToken);

        var login = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = email, Password = TestUsers.Password }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_are_rejected()
    {
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest { Email = $"user-{Guid.NewGuid():N}@example.com", Password = "Short1!" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        using var client = api.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = user.Email, Password = "Wrong-password-123" }, TestContext.Current.CancellationToken);
        }

        var login = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = user.Email, Password = user.Password }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        user.Client.Dispose();
    }

    [Fact]
    public async Task A_refresh_token_gets_a_new_access_token()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        using var client = api.CreateClient();
        var token = await TestUsers.LoginAsync(client, user.Email, user.Password, TestContext.Current.CancellationToken);

        var refreshed = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest { RefreshToken = token.RefreshToken }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        user.Client.Dispose();
    }

    [Fact]
    public async Task Logs_never_contain_passwords_or_tokens()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        await user.Client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        foreach (var record in api.Logs.GetSnapshot())
        {
            var text = string.Join(' ', [record.Message, .. record.StructuredState?.Select(pair => pair.Value) ?? []]);
            Assert.DoesNotContain(user.Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(user.AccessToken, text, StringComparison.Ordinal);
        }

        user.Client.Dispose();
    }
}
