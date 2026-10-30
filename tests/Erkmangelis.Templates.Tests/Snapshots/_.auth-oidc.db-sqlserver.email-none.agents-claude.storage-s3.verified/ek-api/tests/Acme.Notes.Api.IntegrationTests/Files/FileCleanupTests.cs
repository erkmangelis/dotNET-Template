using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests.Files;

public sealed class FileCleanupTests(ApiFactory api)
{
    [Fact]
    public async Task An_object_no_record_points_to_is_removed_and_a_recorded_one_is_kept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = (await TestUsers.SignInNewUserAsync(api)).Client;
        var kept = await TestFiles.UploadOkAsync(client, "kept"u8.ToArray(), "kept.txt");
        var orphan = ObjectKeys.Content(Guid.CreateVersion7());
        var stranger = "files/not-a-file-id";
        await using var scope = api.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        foreach (var key in new[] { orphan, stranger })
        {
            using var content = new MemoryStream("left behind"u8.ToArray());
            await storage.PutAsync(key, content, "text/plain", cancellationToken);
        }

        // Counts everything written so far as old enough.
        await scope.ServiceProvider.GetRequiredService<FileJanitor>().RemoveOrphansAsync(DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);

        Assert.Null(await storage.GetSizeAsync(orphan, cancellationToken));
        Assert.Null(await storage.GetSizeAsync(stranger, cancellationToken));
        Assert.NotNull(await storage.GetSizeAsync(ObjectKeys.Content(kept.Id), cancellationToken));
    }

    [Fact]
    public async Task An_expired_file_is_removed_with_its_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = (await TestUsers.SignInNewUserAsync(api)).Client;
        var file = await TestFiles.UploadOkAsync(client, TestFiles.Png(), "expired.png", temporary: true);
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Only this test's file is moved into the past, so other tests' files are left alone.
        var row = await db.Files.SingleAsync(stored => stored.Id == file.Id, cancellationToken);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(cancellationToken);

        await scope.ServiceProvider.GetRequiredService<FileJanitor>().RemoveExpiredAsync(cancellationToken);

        Assert.False(await db.Files.AnyAsync(stored => stored.Id == file.Id, cancellationToken));
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        Assert.Null(await storage.GetSizeAsync(ObjectKeys.Content(file.Id), cancellationToken));
        Assert.Null(await storage.GetSizeAsync(ObjectKeys.Thumbnail(file.Id), cancellationToken));
    }
}
