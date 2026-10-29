using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests;

public sealed class RecordMetadataTests(ApiFactory api)
{
    [Fact]
    public async Task Saves_record_who_created_and_who_last_changed_a_note()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        var created = await user.Client.PostAsJsonAsync("/v1/notes", new { title = $"Note {Guid.NewGuid():N}" }, TestContext.Current.CancellationToken);
        var note = (await created.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken))!;
        await user.Client.PutAsJsonAsync($"/v1/notes/{note.Id}", new { title = note.Title, content = "Changed" }, TestContext.Current.CancellationToken);

        await using var scope = api.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notes
            .AsNoTracking()
            .SingleAsync(row => row.Id == note.Id, TestContext.Current.CancellationToken);

        Assert.Equal(stored.OwnerId, stored.CreatedBy);
        Assert.Equal(stored.OwnerId, stored.UpdatedBy);
        Assert.True(stored.UpdatedAt > stored.CreatedAt);
    }
}
