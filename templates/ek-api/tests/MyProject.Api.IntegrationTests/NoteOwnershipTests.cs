using System.Net;
using System.Net.Http.Json;

namespace MyProject.Api.IntegrationTests;

/// <summary>One user must never see another user's notes.</summary>
public sealed class NoteOwnershipTests(ApiFactory api)
{
    [Fact]
    public async Task Another_users_note_answers_404()
    {
        var alice = await TestUsers.SignInNewUserAsync(api);
        var bob = await TestUsers.SignInNewUserAsync(api);
        var created = await alice.Client.PostAsJsonAsync("/v1/notes", new { title = "Alice's secret" }, TestContext.Current.CancellationToken);

        var response = await bob.Client.GetAsync(created.Headers.Location, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        alice.Client.Dispose();
        bob.Client.Dispose();
    }

    [Fact]
    public async Task The_list_holds_only_the_callers_notes()
    {
        var alice = await TestUsers.SignInNewUserAsync(api);
        var bob = await TestUsers.SignInNewUserAsync(api);
        await alice.Client.PostAsJsonAsync("/v1/notes", new { title = "Alice's note" }, TestContext.Current.CancellationToken);
        await bob.Client.PostAsJsonAsync("/v1/notes", new { title = "Bob's note" }, TestContext.Current.CancellationToken);

        var page = await bob.Client.GetFromJsonAsync<PagedResult<NoteResponse>>("/v1/notes", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(["Bob's note"], page.Items.Select(note => note.Title));
        alice.Client.Dispose();
        bob.Client.Dispose();
    }

    [Fact]
    public async Task Two_users_may_use_the_same_title()
    {
        var alice = await TestUsers.SignInNewUserAsync(api);
        var bob = await TestUsers.SignInNewUserAsync(api);

        var first = await alice.Client.PostAsJsonAsync("/v1/notes", new { title = "Shopping" }, TestContext.Current.CancellationToken);
        var second = await bob.Client.PostAsJsonAsync("/v1/notes", new { title = "Shopping" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        alice.Client.Dispose();
        bob.Client.Dispose();
    }
}
