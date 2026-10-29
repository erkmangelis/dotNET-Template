namespace MyProject.Api.Features.Notes;

internal interface INoteStore
{
    /// <summary>Saves a new note. Returns false when the owner already has a note with that title.</summary>
    Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken);

    Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken);

#if (UseAuth)
    /// <summary>The owner's notes, or everyone's when <paramref name="ownerId"/> is null, filtered and sorted by <paramref name="query"/>.</summary>
    Task<PagedResult<Note>> ListAsync(string? ownerId, NoteListQuery query, CancellationToken cancellationToken);
#else
    /// <summary>All notes, filtered and sorted by <paramref name="query"/>.</summary>
    Task<PagedResult<Note>> ListAsync(NoteListQuery query, CancellationToken cancellationToken);
#endif
}
