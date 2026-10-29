using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acme.Notes.Api.Features.Notes;

internal sealed class EfNoteStore(AppDbContext db) : INoteStore
{
    private const string LikeEscape = "\\";

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

    /// <summary>Makes the client's text match literally: <c>%</c>, <c>_</c> and the escape character lose their meaning.</summary>
    private static string EscapeLike(string text) =>
        text.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);
}
