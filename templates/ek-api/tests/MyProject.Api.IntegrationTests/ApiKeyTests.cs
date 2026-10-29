using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

/// <summary>API keys act for their owner, only within their scopes, and never on account or admin endpoints.</summary>
public sealed class ApiKeyTests(ApiFactory api)
{
    private static readonly string[] _unknownScopes = ["admin:all"];

    [Fact]
    public async Task A_key_reads_its_owners_notes()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        await owner.Client.PostAsJsonAsync("/v1/notes", new { title = "Visible to my key" }, TestContext.Current.CancellationToken);
        using var key = KeyClient(await CreateKeyAsync(owner.Client, ApiKeyScopes.NotesRead));

        var page = await key.GetFromJsonAsync<PagedResult<NoteResponse>>("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(["Visible to my key"], page!.Items.Select(note => note.Title));
        owner.Client.Dispose();
    }

    [Fact]
    public async Task A_key_needs_the_write_scope_to_create_notes()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        using var reader = KeyClient(await CreateKeyAsync(owner.Client, ApiKeyScopes.NotesRead));
        using var writer = KeyClient(await CreateKeyAsync(owner.Client, ApiKeyScopes.NotesWrite));

        var refused = await reader.PostAsJsonAsync("/v1/notes", new { title = "From the reader" }, TestContext.Current.CancellationToken);
        var created = await writer.PostAsJsonAsync("/v1/notes", new { title = "From the writer" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var mine = await owner.Client.GetFromJsonAsync<PagedResult<NoteResponse>>("/v1/notes", TestContext.Current.CancellationToken);
        Assert.Equal(["From the writer"], mine!.Items.Select(note => note.Title));
        owner.Client.Dispose();
    }

    [Fact]
    public async Task A_key_cannot_manage_keys_or_reach_admin_endpoints()
    {
        var admin = await TestUsers.SignInNewUserAsync(api, Roles.Admin);
        using var key = KeyClient(await CreateKeyAsync(admin.Client, ApiKeyScopes.All));

        var minted = await key.PostAsJsonAsync("/v1/api-keys", new { name = "Escalation", scopes = ApiKeyScopes.All }, TestContext.Current.CancellationToken);
        var listed = await key.GetAsync("/v1/api-keys", TestContext.Current.CancellationToken);
        var adminNotes = await key.GetAsync("/v1/admin/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, minted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, adminNotes.StatusCode);
        admin.Client.Dispose();
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    public async Task Bad_keys_are_refused(string problem)
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var real = await CreateKeyAsync(owner.Client, ApiKeyScopes.NotesRead);
        var key = problem switch
        {
            "tampered" => real[..^1] + (real[^1] == 'A' ? 'B' : 'A'),
            "unknown" => ApiKeyFormat.Generate().Key,
            _ => "not-an-api-key",
        };
        using var client = KeyClient(key);

        var response = await client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        owner.Client.Dispose();
    }

    [Fact]
    public async Task A_revoked_key_is_refused()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var created = await CreateAsync(owner.Client, ApiKeyScopes.NotesRead);
        using var key = KeyClient(created.Key);

        var revoked = await owner.Client.DeleteAsync($"/v1/api-keys/{created.ApiKey.Id}", TestContext.Current.CancellationToken);
        var response = await key.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        owner.Client.Dispose();
    }

    [Fact]
    public async Task An_expired_key_is_refused()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var created = await CreateAsync(owner.Client, ApiKeyScopes.NotesRead);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().ApiKeys
                .Where(key => key.Id == created.ApiKey.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(key => key.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        }

        using var client = KeyClient(created.Key);
        var response = await client.GetAsync("/v1/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        owner.Client.Dispose();
    }

    [Theory]
    [InlineData(0, HttpStatusCode.Unauthorized)]
    [InlineData(24, HttpStatusCode.OK)]
    public async Task Rotating_gives_a_new_key_and_retires_the_old_one_after_the_overlap(int overlapHours, HttpStatusCode oldKeyAfterwards)
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var created = await CreateAsync(owner.Client, ApiKeyScopes.NotesRead);

        var rotation = await owner.Client.PostAsJsonAsync($"/v1/api-keys/{created.ApiKey.Id}/rotate", new { overlapHours }, TestContext.Current.CancellationToken);
        var rotated = await rotation.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(TestContext.Current.CancellationToken);
        using var oldKey = KeyClient(created.Key);
        using var newKey = KeyClient(rotated!.Key);

        Assert.Equal(HttpStatusCode.OK, (await newKey.GetAsync("/v1/notes", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(oldKeyAfterwards, (await oldKey.GetAsync("/v1/notes", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(created.ApiKey.Scopes, rotated.ApiKey.Scopes);
        owner.Client.Dispose();
    }

    [Fact]
    public async Task Only_a_hash_of_the_key_is_stored_and_lists_never_show_it()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var created = await CreateAsync(owner.Client, ApiKeyScopes.NotesRead);
        Assert.True(ApiKeyFormat.TryParse(created.Key, out _, out var secret));

        var listed = await owner.Client.GetStringAsync("/v1/api-keys", TestContext.Current.CancellationToken);
        await using var scope = api.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ApiKeys
            .SingleAsync(key => key.Id == created.ApiKey.Id, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), stored.SecretHash);
        Assert.DoesNotContain(secret, stored.LookupId + stored.Name, StringComparison.Ordinal);
        owner.Client.Dispose();
    }

    [Fact]
    public async Task Nobody_can_revoke_or_rotate_another_users_key()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);
        var other = await TestUsers.SignInNewUserAsync(api);
        var created = await CreateAsync(owner.Client, ApiKeyScopes.NotesRead);

        var revoked = await other.Client.DeleteAsync($"/v1/api-keys/{created.ApiKey.Id}", TestContext.Current.CancellationToken);
        var rotated = await other.Client.PostAsJsonAsync($"/v1/api-keys/{created.ApiKey.Id}/rotate", new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rotated.StatusCode);
        owner.Client.Dispose();
        other.Client.Dispose();
    }

    [Fact]
    public async Task Unknown_scopes_are_rejected()
    {
        var owner = await TestUsers.SignInNewUserAsync(api);

        var response = await owner.Client.PostAsJsonAsync("/v1/api-keys", new { name = "Too much", scopes = _unknownScopes }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        owner.Client.Dispose();
    }

    private HttpClient KeyClient(string key)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
        return client;
    }

    private static async Task<string> CreateKeyAsync(HttpClient owner, params string[] scopes) =>
        (await CreateAsync(owner, scopes)).Key;

    private static async Task<CreatedApiKeyResponse> CreateAsync(HttpClient owner, params string[] scopes)
    {
        var response = await owner.PostAsJsonAsync("/v1/api-keys", new { name = "CI", scopes }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(TestContext.Current.CancellationToken))!;
    }
}
