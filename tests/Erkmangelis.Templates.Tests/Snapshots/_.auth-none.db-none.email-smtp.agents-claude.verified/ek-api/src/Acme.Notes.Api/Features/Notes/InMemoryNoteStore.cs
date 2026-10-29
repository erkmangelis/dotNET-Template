namespace Acme.Notes.Api.Features.Notes;

/// <summary>Keeps notes in process memory. Replace it with a database store when you add one.</summary>
internal sealed class InMemoryNoteStore(TimeProvider time) : INoteStore
{
    private readonly Lock _lock = new();
    private readonly List<Note> _notes = [];

    public Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (HasTitle(note))
            {
                return Task.FromResult(false);
            }

            RecordMetadata.Stamp(note, added: true, RecordMetadata.Now(time));
            _notes.Add(note.Clone());
            return Task.FromResult(true);
        }
    }

    public Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_notes.Find(note => note.Id == id && note.DeletedAt is null)?.Clone());
        }
    }

    public Task<NoteSaveResult> TryUpdateAsync(Note note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (IsStale(note))
            {
                return Task.FromResult(NoteSaveResult.Stale);
            }

            if (HasTitle(note))
            {
                return Task.FromResult(NoteSaveResult.DuplicateTitle);
            }

            RecordMetadata.Stamp(note, added: false, RecordMetadata.Now(time));
            Replace(note);
            return Task.FromResult(NoteSaveResult.Saved);
        }
    }

    public Task<NoteSaveResult> TryDeleteAsync(Note note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (IsStale(note))
            {
                return Task.FromResult(NoteSaveResult.Stale);
            }

            var now = RecordMetadata.Now(time);
            note.DeletedAt = now;
            RecordMetadata.Stamp(note, added: false, now);
            Replace(note);
            return Task.FromResult(NoteSaveResult.Saved);
        }
    }

    public Task<PagedResult<Note>> ListAsync(NoteListQuery query, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var matching = Matching(query);
            var total = matching.LongCount();
            var items = query.Order(matching)
                .Skip(Paging.Skip(query.PageNumber, query.PageLength))
                .Take(query.PageLength)
                .Select(note => note.Clone())
                .ToList();

            return Task.FromResult(new PagedResult<Note>(items, query.PageNumber, query.PageLength, total));
        }
    }

    public async IAsyncEnumerable<Note> StreamAsync(NoteFilter filter)
    {
        List<Note> notes;
        lock (_lock)
        {
            notes = [.. filter.Order(Matching(filter)).Select(note => note.Clone())];
        }

        foreach (var note in notes)
        {
            await Task.Yield();
            yield return note;
        }
    }

    private IQueryable<Note> Matching(NoteFilter filter)
    {
        var matching = filter.Filter(_notes.Where(note => note.DeletedAt is null).AsQueryable());
        return string.IsNullOrEmpty(filter.Search)
            ? matching
            : matching.Where(note => note.Title.Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsStale(Note note) =>
        _notes.Find(existing => existing.Id == note.Id) is not { DeletedAt: null } stored || stored.Version != note.Version;

    private void Replace(Note note)
    {
        note.Version++;
        _notes[_notes.FindIndex(existing => existing.Id == note.Id)] = note.Clone();
    }

    private bool HasTitle(Note note) =>
        _notes.Exists(existing => existing.Id != note.Id
            && existing.DeletedAt is null
            && string.Equals(existing.Title, note.Title, StringComparison.Ordinal));
}
