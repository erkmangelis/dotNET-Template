using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MyProject.Api.IntegrationTests;

/// <summary>Adding a passkey and signing in with it, with a software authenticator standing in for the browser.</summary>
public sealed class PasskeyTests(ApiFactory api)
{
    // The browser sends its origin on the ceremony requests; the relying party id defaults to the API's host.
    private const string Origin = "https://localhost";

    [Fact]
    public async Task A_new_passkey_signs_in_without_a_password()
    {
        using var authenticator = new TestAuthenticator();
        var email = await AddPasskeyAsync(authenticator);
        using var browser = Browser();

        var requestOptions = await PostJsonAsync(browser, "/v1/auth/passkeys/request-options", $$"""{"email":"{{email}}"}""");
        var login = await PostJsonAsync(browser, "/v1/auth/passkeys/login", Wrap(authenticator.Get(requestOptions, Origin)), ensureSuccess: false);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task A_passkey_signs_in_without_an_email_too()
    {
        using var authenticator = new TestAuthenticator();
        await AddPasskeyAsync(authenticator);
        using var browser = Browser();

        var requestOptions = await PostJsonAsync(browser, "/v1/auth/passkeys/request-options", "{}");
        var login = await PostJsonAsync(browser, "/v1/auth/passkeys/login", Wrap(authenticator.Get(requestOptions, Origin)), ensureSuccess: false);

        var tokens = await login.Content.ReadFromJsonAsync<AccessTokenResponse>(TestContext.Current.CancellationToken);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/v1/notes", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_passkey_made_for_another_site_is_refused()
    {
        using var authenticator = new TestAuthenticator();
        using var browser = await SignedInBrowserAsync();

        var creationOptions = await PostJsonAsync(browser, "/v1/auth/passkeys/creation-options", "{}");
        var response = await PostJsonAsync(
            browser,
            "/v1/auth/passkeys",
            Wrap(authenticator.Create(creationOptions, Origin, rpIdOverride: "evil.example")),
            ensureSuccess: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_answer_to_an_old_challenge_is_refused()
    {
        using var authenticator = new TestAuthenticator();
        await AddPasskeyAsync(authenticator);
        using var browser = Browser();

        var stale = await PostJsonAsync(browser, "/v1/auth/passkeys/request-options", "{}");
        await PostJsonAsync(browser, "/v1/auth/passkeys/request-options", "{}");
        var login = await PostJsonAsync(browser, "/v1/auth/passkeys/login", Wrap(authenticator.Get(stale, Origin)), ensureSuccess: false);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task A_removed_passkey_no_longer_signs_in()
    {
        using var authenticator = new TestAuthenticator();
        using var owner = await SignedInBrowserAsync();
        await RegisterAsync(owner, authenticator);

        var listed = await owner.GetFromJsonAsync<PasskeyResponse[]>("/v1/auth/passkeys", TestContext.Current.CancellationToken);
        var removed = await owner.DeleteAsync($"/v1/auth/passkeys/{authenticator.CredentialId}", TestContext.Current.CancellationToken);

        using var browser = Browser();
        var requestOptions = await PostJsonAsync(browser, "/v1/auth/passkeys/request-options", "{}");
        var login = await PostJsonAsync(browser, "/v1/auth/passkeys/login", Wrap(authenticator.Get(requestOptions, Origin)), ensureSuccess: false);

        Assert.Equal("Laptop", Assert.Single(listed!).Name);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private async Task<string> AddPasskeyAsync(TestAuthenticator authenticator)
    {
        using var browser = Browser();
        var email = await TestUsers.RegisterConfirmedUserAsync(api, browser);
        var tokens = await TestUsers.LoginAsync(browser, email, TestUsers.Password, TestContext.Current.CancellationToken);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        await RegisterAsync(browser, authenticator);
        return email;
    }

    private static async Task RegisterAsync(HttpClient browser, TestAuthenticator authenticator)
    {
        var creationOptions = await PostJsonAsync(browser, "/v1/auth/passkeys/creation-options", "{}");
        var credential = authenticator.Create(creationOptions, Origin);
        await PostJsonAsync(browser, "/v1/auth/passkeys", $$"""{"credential":{{credential}},"name":"Laptop"}""");
    }

    private async Task<HttpClient> SignedInBrowserAsync()
    {
        var browser = Browser();
        var email = await TestUsers.RegisterConfirmedUserAsync(api, browser);
        var tokens = await TestUsers.LoginAsync(browser, email, TestUsers.Password, TestContext.Current.CancellationToken);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return browser;
    }

    // HTTPS so the ceremony cookie (Secure) round-trips, and the Origin header a browser would send.
    private HttpClient Browser()
    {
        var browser = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(Origin) });
        browser.DefaultRequestHeaders.Add("Origin", Origin);
        return browser;
    }

    private static string Wrap(string credential) => $$"""{"credential":{{credential}}}""";

    private static async Task<string> PostJsonAsync(HttpClient client, string path, string json) =>
        await (await PostJsonAsync(client, path, json, ensureSuccess: true)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json, bool ensureSuccess)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
        if (ensureSuccess)
        {
            response.EnsureSuccessStatusCode();
        }

        return response;
    }
}
