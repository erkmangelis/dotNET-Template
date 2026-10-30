using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Features.Notes;

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
#if (UseSqlite)
        note.Version = 1;
#endif
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
        var stored = await TrackAsync(note, cancellationToken);
        if (stored is null)
        {
            return NoteSaveResult.Stale;
        }

        db.Entry(stored).CurrentValues.SetValues(note);
        ExpectVersion(stored, note.Version);
        var result = await TrySaveAsync(cancellationToken);
        if (result == NoteSaveResult.Saved)
        {
            // The caller answers with its own copy, so give it what the save filled in.
            note.UpdatedAt = stored.UpdatedAt;
#if (UseAuth)
            note.UpdatedBy = stored.UpdatedBy;
#endif
            note.Version = stored.Version;
        }

        return result;
    }

    public async Task<NoteSaveResult> TryDeleteAsync(Note note, CancellationToken cancellationToken)
    {
        // The save turns the removal into a soft delete; see AuditableEntityInterceptor.
        var stored = await TrackAsync(note, cancellationToken);
        if (stored is null)
        {
            return NoteSaveResult.Stale;
        }

        ExpectVersion(stored, note.Version);
        db.Notes.Remove(stored);
        return await TrySaveAsync(cancellationToken);
    }

#if (UseJobs)
    public Task<int> PurgeDeletedAsync(DateTimeOffset deletedBefore, CancellationToken cancellationToken) =>
        // A bulk delete skips AuditableEntityInterceptor, so it really removes the rows.
        db.Notes.IgnoreQueryFilters([SoftDelete.Filter])
            .Where(note => note.DeletedAt < deletedBefore)
            .ExecuteDeleteAsync(cancellationToken);

#endif
    public Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Notes.AsNoTracking().FirstOrDefaultAsync(note => note.Id == id, cancellationToken);

#if (UseAuth)
    public async Task<PagedResult<Note>> ListAsync(string? ownerId, NoteListQuery query, CancellationToken cancellationToken)
    {
        var notes = Matching(ownerId, query);
#else
    public async Task<PagedResult<Note>> ListAsync(NoteListQuery query, CancellationToken cancellationToken)
    {
        var notes = Matching(query);
#endif
        var total = await notes.LongCountAsync(cancellationToken);
        var items = await query.Order(notes)
            .Skip(Paging.Skip(query.PageNumber, query.PageLength))
            .Take(query.PageLength)
            .ToListAsync(cancellationToken);

        return new PagedResult<Note>(items, query.PageNumber, query.PageLength, total);
    }

#if (UseAuth)
    public async Task<PageSlice<Note>> ListSliceAsync(string ownerId, NoteListQuery query, CancellationToken cancellationToken)
    {
        var notes = Matching(ownerId, query);
#else
    public async Task<PageSlice<Note>> ListSliceAsync(NoteListQuery query, CancellationToken cancellationToken)
    {
        var notes = Matching(query);
#endif
        var items = await query.Order(notes)
            .Skip(Paging.Skip(query.PageNumber, query.PageLength))
            .Take(query.PageLength + 1)
            .ToListAsync(cancellationToken);

        return PageSlice<Note>.FromOneExtra(items, query.PageNumber, query.PageLength);
    }

#if (UseAuth)
    public IAsyncEnumerable<Note> StreamAsync(string ownerId, NoteFilter filter) =>
        filter.Order(Matching(ownerId, filter)).AsAsyncEnumerable();

    private IQueryable<Note> Matching(string? ownerId, NoteFilter filter)
    {
        var notes = ownerId is null ? db.Notes.AsNoTracking() : db.Notes.AsNoTracking().Where(note => note.OwnerId == ownerId);
#else
    public IAsyncEnumerable<Note> StreamAsync(NoteFilter filter) =>
        filter.Order(Matching(filter)).AsAsyncEnumerable();

    private IQueryable<Note> Matching(NoteFilter filter)
    {
        var notes = db.Notes.AsNoTracking();
#endif
        notes = filter.Filter(notes);
        if (!string.IsNullOrEmpty(filter.Search))
        {
            var pattern = $"%{EscapeLike(filter.Search)}%";
#if (UsePostgres)
            notes = notes.Where(note => EF.Functions.ILike(note.Title, pattern, LikeEscape));
#elif (UseSqlServer)
            // Ignores case through the database's default collation.
            notes = notes.Where(note => EF.Functions.Like(note.Title, pattern, LikeEscape));
#else
            // SQLite's LIKE ignores case for ASCII letters only.
            notes = notes.Where(note => EF.Functions.Like(note.Title, pattern, LikeEscape));
#endif
        }

        return notes;
    }

    /// <summary>Whether another note of the same owner already has this note's title. Deleted notes do not count.</summary>
    private Task<bool> HasTitleAsync(Note note, CancellationToken cancellationToken) =>
#if (UseAuth)
        db.Notes.AnyAsync(existing => existing.Id != note.Id && existing.OwnerId == note.OwnerId && existing.Title == note.Title, cancellationToken);
#else
        db.Notes.AnyAsync(existing => existing.Id != note.Id && existing.Title == note.Title, cancellationToken);
#endif

    private Task<Note?> TrackAsync(Note note, CancellationToken cancellationToken) =>
        db.Notes.SingleOrDefaultAsync(row => row.Id == note.Id, cancellationToken);

    /// <summary>
    /// Makes the save's UPDATE match only the row version the caller read, so a change made in between
    /// fails the save instead of being overwritten.
    /// </summary>
#if (UseSqlite)
    private void ExpectVersion(Note stored, ulong version)
    {
        // SQLite has no row version: the UPDATE checks the old value and writes the next one.
        var property = db.Entry(stored).Property(row => row.Version);
        property.OriginalValue = version;
        property.CurrentValue = version + 1;
    }
#elif (UseSqlServer)
    private void ExpectVersion(Note stored, ulong version)
    {
        var property = db.Entry(stored).Property(row => row.Version);
        property.OriginalValue = version;
        property.IsModified = false;
    }
#else
    private void ExpectVersion(Note stored, uint version)
    {
        var property = db.Entry(stored).Property(row => row.Version);
        property.OriginalValue = version;
        property.IsModified = false;
    }
#endif

    private async Task<NoteSaveResult> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return NoteSaveResult.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return NoteSaveResult.Stale;
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            // Drops the failed changes, and the audit entries written for them.
            db.ChangeTracker.Clear();
            return NoteSaveResult.DuplicateTitle;
        }
    }

    /// <summary>Makes the client's text match literally: <c>%</c>, <c>_</c> and the escape character lose their meaning.</summary>
    private static string EscapeLike(string text) =>
        text.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
#if (UseSqlServer)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal)
            .Replace("[", LikeEscape + "[", StringComparison.Ordinal);
#else
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);
#endif
}
