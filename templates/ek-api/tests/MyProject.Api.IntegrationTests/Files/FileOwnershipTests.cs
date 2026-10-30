using System.Net;

namespace MyProject.Api.IntegrationTests.Files;

/// <summary>One user must never reach another user's files, by any route.</summary>
public sealed class FileOwnershipTests(ApiFactory api)
{
    [Theory]
    [InlineData("")]
    [InlineData("/content")]
    [InlineData("/link")]
    [InlineData("/thumbnail")]
    public async Task Another_users_file_answers_404(string route)
    {
        var alice = await TestUsers.SignInNewUserAsync(api);
        var bob = await TestUsers.SignInNewUserAsync(api);
        var file = await TestFiles.UploadOkAsync(alice.Client, TestFiles.Png(), "alice.png");

        using var response = await bob.Client.GetAsync($"/v1/files/{file.Id}{route}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        alice.Client.Dispose();
        bob.Client.Dispose();
    }

    [Fact]
    public async Task Another_user_cannot_delete_or_complete_a_file()
    {
        var alice = await TestUsers.SignInNewUserAsync(api);
        var bob = await TestUsers.SignInNewUserAsync(api);
        var file = await TestFiles.UploadOkAsync(alice.Client, TestFiles.Png(), "alice.png");

        using var deleted = await bob.Client.DeleteAsync($"/v1/files/{file.Id}", TestContext.Current.CancellationToken);
        using var completed = await bob.Client.PostAsync($"/v1/files/{file.Id}/complete", null, TestContext.Current.CancellationToken);
        using var stillThere = await alice.Client.GetAsync($"/v1/files/{file.Id}/content", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, completed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        alice.Client.Dispose();
        bob.Client.Dispose();
    }

    [Fact]
    public async Task The_list_holds_only_the_callers_files()
    {
        var alice = await TestUsers.SignInNewUserAsync(api);
        var bob = await TestUsers.SignInNewUserAsync(api);
        await TestFiles.UploadOkAsync(alice.Client, TestFiles.Png(), "alice.png");
        await TestFiles.UploadOkAsync(bob.Client, TestFiles.Png(), "bob.png");

        var page = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<PagedResult<FileResponse>>(bob.Client, "/v1/files", TestContext.Current.CancellationToken);

        Assert.Equal(["bob.png"], page!.Items.Select(file => file.FileName));
        alice.Client.Dispose();
        bob.Client.Dispose();
    }

    [Fact]
    public async Task Signed_out_callers_get_401()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/v1/files", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
