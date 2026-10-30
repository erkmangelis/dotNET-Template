using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Features.Files;

/// <summary>
/// Removes what no one should keep: files whose time is up (temporary files, uploads never completed,
/// refused files), objects in storage that no record points to, and abandoned tus uploads.
/// </summary>
internal sealed partial class FileJanitor(
    AppDbContext db,
    IObjectStorage storage,
    TusUploadStore tus,
    TimeProvider time,
    ILogger<FileJanitor> logger)
{
    private const int BatchSize = 100;

    public async Task RunAsync(TimeSpan orphanGrace, CancellationToken cancellationToken)
    {
        var expired = await RemoveExpiredAsync(cancellationToken);
        var orphans = await RemoveOrphansAsync(time.GetUtcNow() - orphanGrace, cancellationToken);
        var uploads = await tus.RemoveExpiredAsync(cancellationToken);
        if (expired + orphans + uploads > 0)
        {
            LogCleaned(logger, expired, orphans, uploads);
        }
    }

    /// <summary>Deletes files whose <see cref="StoredFile.ExpiresAt"/> has passed: the content first, then the record.</summary>
    public async Task<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var removed = 0;
        while (true)
        {
            var batch = await db.Files
                .Where(file => file.ExpiresAt != null && file.ExpiresAt < now)
                .OrderBy(file => file.ExpiresAt)
                .Take(BatchSize)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return removed;
            }

            foreach (var file in batch)
            {
                await storage.DeleteAsync(file.ObjectKey, cancellationToken);
                await storage.DeleteAsync(file.ThumbnailKey, cancellationToken);
            }

            // A bulk delete, so another instance cleaning up the same rows at the same time is no error.
            var ids = batch.Select(file => file.Id).ToList();
            removed += await db.Files.Where(file => ids.Contains(file.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>Deletes objects last written before <paramref name="olderThan"/> that no file record points to.</summary>
    public async Task<int> RemoveOrphansAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var prefix in new[] { ObjectKeys.ContentPrefix, ObjectKeys.ThumbnailPrefix })
        {
            var candidates = new List<(string Key, Guid? Id)>();
            await foreach (var item in storage.ListAsync(prefix, cancellationToken))
            {
                if (item.LastModified < olderThan)
                {
                    candidates.Add((item.Key, ObjectKeys.FileId(item.Key)));
                }

                if (candidates.Count == BatchSize)
                {
                    removed += await RemoveUnknownAsync(candidates, cancellationToken);
                    candidates.Clear();
                }
            }

            removed += await RemoveUnknownAsync(candidates, cancellationToken);
        }

        return removed;
    }

    private async Task<int> RemoveUnknownAsync(List<(string Key, Guid? Id)> candidates, CancellationToken cancellationToken)
    {
        var ids = candidates.Where(candidate => candidate.Id is not null).Select(candidate => candidate.Id!.Value).ToList();
        var known = (await db.Files.Where(file => ids.Contains(file.Id)).Select(file => file.Id).ToListAsync(cancellationToken)).ToHashSet();
        var removed = 0;
        foreach (var (key, id) in candidates)
        {
            if (id is not { } fileId || !known.Contains(fileId))
            {
                await storage.DeleteAsync(key, cancellationToken);
                removed++;
            }
        }

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "File cleanup removed {Expired} expired files, {Orphans} orphaned objects and {Uploads} abandoned uploads")]
    private static partial void LogCleaned(ILogger logger, int expired, int orphans, int uploads);
}

/// <summary>Runs <see cref="FileJanitor"/> every <c>Files:CleanupIntervalMinutes</c>. Every step is safe to repeat, so each instance may run it.</summary>
internal sealed class FileCleanupLoop(IServiceScopeFactory scopes, IOptions<FilesOptions> options, TimeProvider time, ILogger<FileCleanupLoop> logger)
    : ScopedBackgroundLoop(scopes, time, logger)
{
    protected override string Name => "file cleanup";

    protected override TimeSpan Interval => TimeSpan.FromMinutes(options.Value.CleanupIntervalMinutes);

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<FileJanitor>().RunAsync(TimeSpan.FromHours(options.Value.OrphanGraceHours), cancellationToken);
}
