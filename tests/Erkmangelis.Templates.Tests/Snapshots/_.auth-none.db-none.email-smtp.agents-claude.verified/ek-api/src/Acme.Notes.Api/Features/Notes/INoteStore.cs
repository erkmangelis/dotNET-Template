namespace Acme.Notes.Api.Features.Notes;

internal enum NoteSaveResult
{
    Saved,

    /// <summary>The owner already has another note with that title.</summary>
    DuplicateTitle,
}

internal interface INoteStore
{
    /// <summary>Saves a new note. Returns false when the owner already has a note with that title.</summary>
    Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken);

    /// <summary>A copy of the note: changing it changes nothing until it is passed to <see cref="TryUpdateAsync"/>.</summary>
    /// <remarks>Deleted notes are never found.</remarks>
    Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Saves changes to a note read with <see cref="FindAsync"/>.</summary>
    Task<NoteSaveResult> TryUpdateAsync(Note note, CancellationToken cancellationToken);

    /// <summary>Marks a note read with <see cref="FindAsync"/> deleted. From then on no read returns it.</summary>
    Task DeleteAsync(Note note, CancellationToken cancellationToken);

    /// <summary>All notes, filtered and sorted by <paramref name="query"/>.</summary>
    Task<PagedResult<Note>> ListAsync(NoteListQuery query, CancellationToken cancellationToken);
}
