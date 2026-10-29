using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acme.Notes.Api.Features.Notes;

internal sealed class EfNoteStore(AppDbContext db) : INoteStore
{
    private const string LikeEscape = "\\";

    public async Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken)
    {
        if (await HasTitleAsync(note, cancellationToken))
        {
            return false;
        }

        // The unique index still decides when two requests race past the check above.
        db.Notes.Add(note);
        return await TrySaveAsync(cancellationToken) == NoteSaveResult.Saved;
    }

    public async Task<NoteSaveResult> TryUpdateAsync(Note note, CancellationToken cancellationToken)
    {
        if (await HasTitleAsync(note, cancellationToken))
        {
            return NoteSaveResult.DuplicateTitle;
        }

        // Copied onto the stored row, so the audit history sees the values before and after.
        var stored = await db.Notes.SingleAsync(row => row.Id == note.Id, cancellationToken);
        db.Entry(stored).CurrentValues.SetValues(note);
        var result = await TrySaveAsync(cancellationToken);
        if (result == NoteSaveResult.Saved)
        {
            // The caller answers with its own copy, so give it what the save filled in.
            note.UpdatedAt = stored.UpdatedAt;
            note.UpdatedBy = stored.UpdatedBy;
        }

        return result;
    }

    public async Task DeleteAsync(Note note, CancellationToken cancellationToken)
    {
        // The save turns the removal into a soft delete; see AuditableEntityInterceptor.
        var stored = await db.Notes.SingleAsync(row => row.Id == note.Id, cancellationToken);
        db.Notes.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Notes.AsNoTracking().FirstOrDefaultAsync(note => note.Id == id, cancellationToken);

    public async Task<PagedResult<Note>> ListAsync(string? ownerId, NoteListQuery query, CancellationToken cancellationToken)
    {
        var notes = ownerId is null ? db.Notes.AsNoTracking() : db.Notes.AsNoTracking().Where(note => note.OwnerId == ownerId);
        notes = query.Filter(notes);
        if (!string.IsNullOrEmpty(query.Search))
        {
            var pattern = $"%{EscapeLike(query.Search)}%";
            notes = notes.Where(note => EF.Functions.ILike(note.Title, pattern, LikeEscape));
        }

        var total = await notes.LongCountAsync(cancellationToken);
        var items = await query.Order(notes)
            .Skip(Paging.Skip(query.PageNumber, query.PageLength))
            .Take(query.PageLength)
            .ToListAsync(cancellationToken);

        return new PagedResult<Note>(items, query.PageNumber, query.PageLength, total);
    }

    /// <summary>Whether another note of the same owner already has this note's title. Deleted notes do not count.</summary>
    private Task<bool> HasTitleAsync(Note note, CancellationToken cancellationToken) =>
        db.Notes.AnyAsync(existing => existing.Id != note.Id && existing.OwnerId == note.OwnerId && existing.Title == note.Title, cancellationToken);

    private async Task<NoteSaveResult> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return NoteSaveResult.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Drops the failed changes, and the audit entries written for them.
            db.ChangeTracker.Clear();
            return NoteSaveResult.DuplicateTitle;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Makes the client's text match literally: <c>%</c>, <c>_</c> and the escape character lose their meaning.</summary>
    private static string EscapeLike(string text) =>
        text.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);
}
