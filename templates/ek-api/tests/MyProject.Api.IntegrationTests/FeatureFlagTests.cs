using System.Net;
#if (UseAuth)
using System.Net.Http.Headers;
#endif
using Microsoft.AspNetCore.Mvc.Testing;

namespace MyProject.Api.IntegrationTests;

public sealed class FeatureFlagTests(ApiFactory api)
{
    [Fact]
    public async Task The_export_is_on_by_default()
    {
        using var client = await ClientAsync(api);

        var response = await client.GetAsync("/v1/notes/export", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Turning_the_flag_off_hides_the_export()
    {
        await using var factory = api.WithWebHostBuilder(builder => builder.UseSetting($"FeatureManagement:{FeatureFlags.NoteExport}", "false"));
        using var client = await ClientAsync(factory);

        var response = await client.GetAsync("/v1/notes/export", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

#if (UseAuth)
    /// <summary>A signed-in client of <paramref name="app"/>; its token comes from the shared test app.</summary>
    private async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> app)
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        user.Client.Dispose();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        return client;
    }
#else
    private static Task<HttpClient> ClientAsync(WebApplicationFactory<Program> app) => Task.FromResult(app.CreateClient());
#endif
}
