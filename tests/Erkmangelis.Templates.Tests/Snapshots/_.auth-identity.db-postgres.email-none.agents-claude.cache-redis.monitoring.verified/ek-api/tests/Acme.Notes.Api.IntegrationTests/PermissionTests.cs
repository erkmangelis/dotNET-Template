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

    [Fact]
    public async Task An_auditor_cannot_change_roles()
    {
        var auditor = await TestUsers.SignInNewUserAsync(api, Roles.Auditor);
        var member = await TestUsers.SignInNewUserAsync(api);
        var memberId = await IdOfAsync(auditor.Client, member.Email);

        var response = await SetRolesAsync(auditor.Client, memberId, Roles.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        auditor.Client.Dispose();
        member.Client.Dispose();
    }

    [Fact]
    public async Task An_admin_grants_a_role_that_applies_from_the_next_sign_in()
    {
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);
        var member = await TestUsers.SignInNewUserAsync(api);
        var memberId = await IdOfAsync(admin.Client, member.Email);

        var granted = await SetRolesAsync(admin.Client, memberId, Roles.Auditor);
        using var client = api.CreateClient();
        var tokens = await TestUsers.LoginAsync(client, member.Email, member.Password, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/notes", TestContext.Current.CancellationToken)).StatusCode);
        admin.Client.Dispose();
        member.Client.Dispose();
    }

    [Fact]
    public async Task Unknown_roles_are_rejected()
    {
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);
        var member = await TestUsers.SignInNewUserAsync(api);
        var memberId = await IdOfAsync(admin.Client, member.Email);

        var response = await SetRolesAsync(admin.Client, memberId, "superuser");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        admin.Client.Dispose();
        member.Client.Dispose();
    }

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_role()
    {
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);
        var adminId = await IdOfAsync(admin.Client, admin.Email);

        var response = await SetRolesAsync(admin.Client, adminId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        admin.Client.Dispose();
    }

    private static Task<HttpResponseMessage> SetRolesAsync(HttpClient client, string userId, params string[] roles) =>
        client.PutAsJsonAsync($"/v1/admin/users/{userId}/roles", new { roles }, TestContext.Current.CancellationToken);

    private static async Task<string> IdOfAsync(HttpClient admin, string email)
    {
        // Auditors and admins can list users; walk the pages until the email shows up.
        for (var page = 1; ; page++)
        {
            var users = await admin.GetFromJsonAsync<PagedResult<AdminUserResponse>>($"/v1/admin/users?page={page}&pageSize=100", TestContext.Current.CancellationToken);
            if (users!.Items.FirstOrDefault(user => user.Email == email) is { } match)
            {
                return match.Id;
            }

            Assert.True(users.HasNextPage, $"{email} is not in the user list.");
        }
    }
}
