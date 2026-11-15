using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>Cookie sessions for a browser frontend: hardened cookies, antiforgery on every unsafe request.</summary>
public sealed class BrowserSessionTests(ApiFactory api)
{
    [Fact]
    public async Task The_session_cookie_is_host_only_secure_http_only_and_strict()
    {
        using var browser = Browser();
        var email = await TestUsers.RegisterConfirmedUserAsync(api, browser);

        var login = await LoginWithCookiesAsync(browser, email);

        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cookie_session_reads_without_a_token_but_writes_only_with_one()
    {
        using var browser = Browser();
        await LoginWithCookiesAsync(browser, await TestUsers.RegisterConfirmedUserAsync(api, browser));

        var read = await browser.GetAsync("/v1/notes", TestContext.Current.CancellationToken);
        var forged = await browser.PostAsJsonAsync("/v1/notes", new { title = "Cross-site write" }, TestContext.Current.CancellationToken);

        await AddAntiforgeryTokenAsync(browser);
        var written = await browser.PostAsJsonAsync("/v1/notes", new { title = "Real write" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        using var browser = Browser();
        await LoginWithCookiesAsync(browser, await TestUsers.RegisterConfirmedUserAsync(api, browser));
        await AddAntiforgeryTokenAsync(browser);

        var logout = await browser.PostAsync("/v1/auth/logout", content: null, TestContext.Current.CancellationToken);
        var after = await browser.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Signed_out_browsers_get_401_not_a_redirect()
    {
        using var browser = Browser();

        var response = await browser.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Secure cookies need HTTPS, and the browser must not follow redirects so a login redirect would show up.
    private HttpClient Browser() =>
        api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> LoginWithCookiesAsync(HttpClient browser, string email)
    {
        var login = await browser.PostAsJsonAsync(
            "/v1/auth/login?useCookies=true",
            new LoginRequest { Email = email, Password = TestUsers.Password },
            TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        return login;
    }

    private static async Task AddAntiforgeryTokenAsync(HttpClient browser)
    {
        var antiforgery = await browser.GetFromJsonAsync<AntiforgeryTokenResponse>("/v1/auth/antiforgery", TestContext.Current.CancellationToken);
        browser.DefaultRequestHeaders.Add(antiforgery!.HeaderName, antiforgery.Token);
    }
}
