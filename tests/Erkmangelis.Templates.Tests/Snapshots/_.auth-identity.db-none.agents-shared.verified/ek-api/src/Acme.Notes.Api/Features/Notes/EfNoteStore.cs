using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acme.Notes.Api.Features.Notes;

internal sealed class EfNoteStore(AppDbContext db) : INoteStore
{
    public async Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken)
    {
        if (await db.Notes.AnyAsync(existing => existing.OwnerId == note.OwnerId && existing.Title == note.Title, cancellationToken))
        {
            return false;
        }

        // The unique index still decides when two requests race past the check above.
        db.Notes.Add(note);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(note).State = EntityState.Detached;
            return false;
        }
    }

    public Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Notes.AsNoTracking().FirstOrDefaultAsync(note => note.Id == id, cancellationToken);

    public async Task<PagedResult<Note>> ListAsync(string ownerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Notes.AsNoTracking().Where(note => note.OwnerId == ownerId);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(note => note.CreatedAt)
            .ThenByDescending(note => note.Id)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Note>(items, page, pageSize, total);
    }
}
