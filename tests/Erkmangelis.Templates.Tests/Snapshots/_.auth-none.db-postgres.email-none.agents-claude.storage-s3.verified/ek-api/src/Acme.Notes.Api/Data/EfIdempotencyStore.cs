using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

/// <summary>
/// Keeps idempotency keys in the database, so a retry that reaches another instance or arrives after
/// a restart still replays the first response. Nothing here stays tracked: the endpoint's own saves
/// share this context.
/// </summary>
internal sealed class EfIdempotencyStore(AppDbContext db, TimeProvider time) : IIdempotencyStore
{
    public async Task<IdempotencyRecord?> TryReserveAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        await db.IdempotencyRecords
            .Where(row => row.Scope == record.Scope && row.Key == record.Key && row.ExpiresAt <= time.GetUtcNow())
            .ExecuteDeleteAsync(cancellationToken);

        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            // Another request holds the key: report what it stored.
            return await db.IdempotencyRecords.AsNoTracking()
                .SingleAsync(row => row.Scope == record.Scope && row.Key == record.Key, cancellationToken);
        }
        finally
        {
            db.Entry(record).State = EntityState.Detached;
        }
    }

    public Task CompleteAsync(IdempotencyRecord record, CancellationToken cancellationToken) =>
        db.IdempotencyRecords
            .Where(row => row.Scope == record.Scope && row.Key == record.Key)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.StatusCode, record.StatusCode)
                    .SetProperty(row => row.Headers, record.Headers)
                    .SetProperty(row => row.Body, record.Body),
                cancellationToken);

    public Task ReleaseAsync(IdempotencyRecord record, CancellationToken cancellationToken) =>
        db.IdempotencyRecords
            .Where(row => row.Scope == record.Scope && row.Key == record.Key)
            .ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        db.IdempotencyRecords.Where(row => row.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
}
