namespace MyProject.Api.Features.Notes;

/// <summary>Keeps notes in process memory. Replace it with a database store when you add one.</summary>
internal sealed class InMemoryNoteStore : INoteStore
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

            _notes.Add(note);
            return Task.FromResult(true);
        }
    }

    public Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_notes.Find(note => note.Id == id));
        }
    }

    public Task<PagedResult<Note>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var items = _notes
                .OrderByDescending(note => note.CreatedAt)
                .ThenByDescending(note => note.Id)
                .Skip(Paging.Skip(page, pageSize))
                .Take(pageSize)
                .ToList();

            return Task.FromResult(new PagedResult<Note>(items, page, pageSize, _notes.Count));
        }
    }
}
