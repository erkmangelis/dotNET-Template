using System.Net;
using System.Net.Http.Json;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>Admin endpoints check permissions, which come from roles. Everyone else is refused.</summary>
public sealed class PermissionTests(ApiFactory api)
{
    [Fact]
    public async Task Signed_out_callers_get_401()
    {
        using var anonymous = api.CreateClient();

        var response = await anonymous.GetAsync("/v1/admin/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_without_the_permission_get_403()
    {
        var member = await TestUsers.SignInNewUserAsync(api);

        var response = await member.Client.GetAsync("/v1/admin/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        member.Client.Dispose();
    }

    [Fact]
    public async Task An_auditor_reads_everyones_notes()
    {
        var author = await TestUsers.SignInNewUserAsync(api);
        var title = $"Seen by audit {Guid.NewGuid():N}";
        await author.Client.PostAsJsonAsync("/v1/notes", new { title }, TestContext.Current.CancellationToken);
        var auditor = await TestUsers.SignInNewUserAsync(api, Roles.Auditor);

        var page = await auditor.Client.GetFromJsonAsync<PagedResult<AdminNoteResponse>>("/v1/admin/notes?pageSize=100", TestContext.Current.CancellationToken);

        Assert.Contains(page!.Items, note => note.Title == title);
        author.Client.Dispose();
        auditor.Client.Dispose();
    }
}
