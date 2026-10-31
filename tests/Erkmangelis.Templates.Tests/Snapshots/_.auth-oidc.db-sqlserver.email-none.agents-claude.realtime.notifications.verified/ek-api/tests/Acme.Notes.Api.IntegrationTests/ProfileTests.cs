using System.Net;
using System.Net.Http.Json;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class ProfileTests(ApiFactory api)
{
    [Fact]
    public async Task A_new_user_sees_default_preferences_and_their_permissions()
    {
        var auditor = await TestUsers.SignInNewUserAsync(api, Roles.Auditor);

        var me = await auditor.Client.GetFromJsonAsync<MeResponse>("/v1/me", TestContext.Current.CancellationToken);

        Assert.Equal(new ProfileResponse(null, "UTC", "en", "system"), me!.Profile);
        Assert.Contains(Roles.Auditor, me.Roles);
        Assert.Contains(Permissions.NotesReadAll, me.Permissions);
        auditor.Client.Dispose();
    }

    [Fact]
    public async Task Saved_preferences_come_back_and_stay_private()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var other = await TestUsers.SignInNewUserAsync(api);

        var saved = await owner.Client.PutAsJsonAsync(
            "/v1/me/profile",
            new { displayName = "  Ada  ", timeZone = "Europe/Istanbul", locale = "tr-TR", theme = "dark" },
            TestContext.Current.CancellationToken);
        var mine = await owner.Client.GetFromJsonAsync<MeResponse>("/v1/me", TestContext.Current.CancellationToken);
        var theirs = await other.Client.GetFromJsonAsync<MeResponse>("/v1/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(new ProfileResponse("Ada", "Europe/Istanbul", "tr-TR", "dark"), mine!.Profile);
        Assert.Equal(new ProfileResponse(null, "UTC", "en", "system"), theirs!.Profile);
        owner.Client.Dispose();
        other.Client.Dispose();
    }

    [Fact]
    public async Task A_profile_update_cannot_target_another_user()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var victim = await TestUsers.SignInNewUserAsync(api);
        var victimId = (await victim.Client.GetFromJsonAsync<MeResponse>("/v1/me", TestContext.Current.CancellationToken))!.Id;

        await owner.Client.PutAsJsonAsync(
            "/v1/me/profile",
            new { userId = victimId, displayName = "Hijacked", timeZone = "UTC", locale = "en", theme = "light" },
            TestContext.Current.CancellationToken);
        var victimMe = await victim.Client.GetFromJsonAsync<MeResponse>("/v1/me", TestContext.Current.CancellationToken);

        Assert.Null(victimMe!.Profile.DisplayName);
        owner.Client.Dispose();
        victim.Client.Dispose();
    }

    [Theory]
    [InlineData("Mars/Olympus", "en", "dark")]
    [InlineData("UTC", "not a locale", "dark")]
    [InlineData("UTC", "en", "neon")]
    public async Task Invalid_preferences_are_rejected(string timeZone, string locale, string theme)
    {
        var owner = await TestUsers.SignInNewUserAsync(api);

        var response = await owner.Client.PutAsJsonAsync("/v1/me/profile", new { timeZone, locale, theme }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        owner.Client.Dispose();
    }

    [Fact]
    public async Task Anonymous_callers_and_api_keys_cannot_see_a_profile()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var created = await owner.Client.PostAsJsonAsync("/v1/api-keys", new { name = "CI", scopes = ApiKeyScopes.All }, TestContext.Current.CancellationToken);
        var key = (await created.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(TestContext.Current.CancellationToken))!.Key;
        using var anonymous = api.CreateClient();
        using var keyClient = api.CreateClient();
        keyClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/v1/me", TestContext.Current.CancellationToken)).StatusCode);
        owner.Client.Dispose();
    }
}
