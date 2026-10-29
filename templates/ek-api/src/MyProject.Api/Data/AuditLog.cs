using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Data;

/// <summary>Reads the audit history, newest first.</summary>
internal sealed class AuditLog(AppDbContext db)
{
#if (UseAuth)
    public async Task<PagedResult<AuditEntry>> ListAsync(
        string? entityType,
        string? entityId,
        string? actorId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
#else
    public async Task<PagedResult<AuditEntry>> ListAsync(
        string? entityType,
        string? entityId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
#endif
    {
        var entries = db.AuditEntries.AsNoTracking();
        if (entityType is not null)
        {
            entries = entries.Where(entry => entry.EntityType == entityType);
        }

        if (entityId is not null)
        {
            entries = entries.Where(entry => entry.EntityId == entityId);
        }
#if (UseAuth)

        if (actorId is not null)
        {
            entries = entries.Where(entry => entry.ActorId == actorId);
        }
#endif

        var total = await entries.LongCountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditEntry>(items, page, pageSize, total);
    }
}
