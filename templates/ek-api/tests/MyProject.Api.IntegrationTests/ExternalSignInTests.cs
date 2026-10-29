using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

/// <summary>Sign-in with a provider's ID token, and the rules for linking it to an account.</summary>
public sealed class ExternalSignInTests(ApiFactory api)
{
    [Theory]
    [InlineData(true)]
    [InlineData("true")] // Apple sends the flag as a string.
    public async Task A_verified_email_creates_an_account_and_signs_in(object emailVerified)
    {
        using var client = api.CreateClient();

        var tokens = await SignInAsync(client, GoogleToken(NewSubject(), NewEmail(), emailVerified));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var notes = await client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
    }

    [Fact]
    public async Task The_same_login_signs_in_to_the_same_account()
    {
        var subject = NewSubject();
        var email = NewEmail();
        using var first = await SignedInClientAsync(GoogleToken(subject, email, emailVerified: true));
        using var second = await SignedInClientAsync(GoogleToken(subject, email, emailVerified: true));

        await first.PostAsJsonAsync("/v1/notes", new { title = "From the first sign-in" }, TestContext.Current.CancellationToken);

        var page = await second.GetFromJsonAsync<PagedResult<NoteResponse>>("/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(["From the first sign-in"], page!.Items.Select(note => note.Title));
    }

    [Fact]
    public async Task A_verified_email_joins_the_existing_account()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        await user.Client.PostAsJsonAsync("/v1/notes", new { title = "Made with a password" }, TestContext.Current.CancellationToken);

        using var google = await SignedInClientAsync(GoogleToken(NewSubject(), user.Email, emailVerified: true));

        var page = await google.GetFromJsonAsync<PagedResult<NoteResponse>>("/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(["Made with a password"], page!.Items.Select(note => note.Title));
        user.Client.Dispose();
    }

    [Fact]
    public async Task An_unverified_email_cannot_take_over_an_existing_account()
    {
        var victim = await TestUsers.SignInNewUserAsync(api);
        using var attacker = api.CreateClient();

        var response = await attacker.PostAsJsonAsync(
            "/v1/auth/external/google",
            new { idToken = GoogleToken(NewSubject(), victim.Email, emailVerified: false) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await LoginsOfAsync(victim.Email));
        victim.Client.Dispose();
    }

    [Fact]
    public async Task A_token_without_an_email_cannot_create_an_account()
    {
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/external/google",
            new { idToken = TestTokens.Create(TestTokens.GoogleIssuer, TestTokens.GoogleClientId, new() { ["sub"] = NewSubject() }) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_verified_sign_in_removes_the_password_of_an_unconfirmed_account()
    {
        // Someone registered this email with a password but never confirmed it. The real owner then signs in with Google.
        var email = NewEmail();
        using var squatter = api.CreateClient();
        await squatter.PostAsJsonAsync("/v1/auth/register", new RegisterRequest { Email = email, Password = TestUsers.Password }, TestContext.Current.CancellationToken);

        using var owner = await SignedInClientAsync(GoogleToken(NewSubject(), email, emailVerified: true));

        var login = await squatter.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = email, Password = TestUsers.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Single(await LoginsOfAsync(email));
    }

    [Fact]
    public async Task A_signed_in_user_can_link_a_login_whatever_its_email()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        var token = GoogleToken(NewSubject(), NewEmail(), emailVerified: false);

        var linked = await user.Client.PostAsJsonAsync("/v1/auth/external/google/link", new { idToken = token }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        using var client = await SignedInClientAsync(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/notes", TestContext.Current.CancellationToken)).StatusCode);
        user.Client.Dispose();
    }

    [Fact]
    public async Task A_login_linked_to_another_account_cannot_be_linked_again()
    {
        var token = GoogleToken(NewSubject(), NewEmail(), emailVerified: true);
        using var owner = await SignedInClientAsync(token);
        var other = await TestUsers.SignInNewUserAsync(api);

        var linked = await other.Client.PostAsJsonAsync("/v1/auth/external/google/link", new { idToken = token }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, linked.StatusCode);
        other.Client.Dispose();
    }

    [Fact]
    public async Task The_last_way_to_sign_in_cannot_be_unlinked()
    {
        using var client = await SignedInClientAsync(GoogleToken(NewSubject(), NewEmail(), emailVerified: true));

        var response = await client.DeleteAsync("/v1/auth/external/google", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("another audience")]
    [InlineData("another issuer")]
    [InlineData("untrusted key")]
    [InlineData("expired")]
    [InlineData("wrong nonce")]
    public async Task Invalid_tokens_are_refused(string problem)
    {
        var claims = new Dictionary<string, object> { ["sub"] = NewSubject(), ["email"] = NewEmail(), ["email_verified"] = true, ["nonce"] = "expected" };
        var token = problem switch
        {
            "another audience" => TestTokens.Create(TestTokens.GoogleIssuer, "someone-elses-app", claims),
            "another issuer" => TestTokens.Create("https://evil.example.com", TestTokens.GoogleClientId, claims),
            "untrusted key" => TestTokens.Create(TestTokens.GoogleIssuer, TestTokens.GoogleClientId, claims, signingKey: TestTokens.UntrustedKey),
            "expired" => TestTokens.Create(TestTokens.GoogleIssuer, TestTokens.GoogleClientId, claims, expires: DateTime.UtcNow.AddMinutes(-10)),
            _ => TestTokens.Create(TestTokens.GoogleIssuer, TestTokens.GoogleClientId, claims),
        };
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/external/google",
            new { idToken = token, nonce = problem == "wrong nonce" ? "replayed" : "expected" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_provider_without_a_client_id_is_off()
    {
        using var client = api.CreateClient();

        var providers = await client.GetFromJsonAsync<ExternalProvidersResponse>("/v1/auth/external", TestContext.Current.CancellationToken);
        var apple = await client.PostAsJsonAsync("/v1/auth/external/apple", new { idToken = "x" }, TestContext.Current.CancellationToken);

        Assert.Equal(["Google"], providers!.Providers);
        Assert.Equal(HttpStatusCode.NotFound, apple.StatusCode);
    }

    private static string NewSubject() => Guid.NewGuid().ToString("N");

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static string GoogleToken(string subject, string email, object emailVerified) =>
        TestTokens.Create(TestTokens.GoogleIssuer, TestTokens.GoogleClientId, new()
        {
            ["sub"] = subject,
            ["email"] = email,
            ["email_verified"] = emailVerified,
        });

    private static async Task<AccessTokenResponse> SignInAsync(HttpClient client, string idToken)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/external/google", new { idToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccessTokenResponse>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpClient> SignedInClientAsync(string idToken)
    {
        var client = api.CreateClient();
        var tokens = await SignInAsync(client, idToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<IList<UserLoginInfo>> LoginsOfAsync(string email)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);
        return user is null ? [] : await users.GetLoginsAsync(user);
    }
}
