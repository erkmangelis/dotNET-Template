namespace MyProject.Api.Features.Notes;

/// <summary>Keeps notes in process memory. Replace it with a database store when you add one.</summary>
internal sealed class InMemoryNoteStore(TimeProvider time) : INoteStore
{
    private readonly Lock _lock = new();
    private readonly List<Note> _notes = [];

    public Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_notes.Exists(existing => string.Equals(existing.Title, note.Title, StringComparison.Ordinal)))
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
            return Task.FromResult(_notes.Find(note => note.Id == id)?.Clone());
        }
    }

    public Task<NoteSaveResult> TryUpdateAsync(Note note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_notes.Exists(existing => existing.Id != note.Id && string.Equals(existing.Title, note.Title, StringComparison.Ordinal)))
            {
                return Task.FromResult(NoteSaveResult.DuplicateTitle);
            }

            RecordMetadata.Stamp(note, added: false, RecordMetadata.Now(time));
            _notes[_notes.FindIndex(existing => existing.Id == note.Id)] = note.Clone();
            return Task.FromResult(NoteSaveResult.Saved);
        }
    }

    public Task<PagedResult<Note>> ListAsync(NoteListQuery query, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var matching = query.Filter(_notes.AsQueryable());
            if (!string.IsNullOrEmpty(query.Search))
            {
                matching = matching.Where(note => note.Title.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
            }

            var total = matching.LongCount();
            var items = query.Order(matching)
                .Skip(Paging.Skip(query.PageNumber, query.PageLength))
                .Take(query.PageLength)
                .Select(note => note.Clone())
                .ToList();

            return Task.FromResult(new PagedResult<Note>(items, query.PageNumber, query.PageLength, total));
        }
    }
}
