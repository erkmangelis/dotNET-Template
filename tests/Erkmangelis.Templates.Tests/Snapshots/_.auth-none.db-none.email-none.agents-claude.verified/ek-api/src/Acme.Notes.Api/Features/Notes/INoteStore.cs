namespace Acme.Notes.Api.Features.Notes;

internal interface INoteStore
{
    /// <summary>Saves a new note. Returns false when the owner already has a note with that title.</summary>
    Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken);

    Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>All notes, filtered and sorted by <paramref name="query"/>.</summary>
    Task<PagedResult<Note>> ListAsync(NoteListQuery query, CancellationToken cancellationToken);
}
