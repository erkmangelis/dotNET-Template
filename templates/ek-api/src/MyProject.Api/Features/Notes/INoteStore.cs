namespace MyProject.Api.Features.Notes;

internal interface INoteStore
{
    /// <summary>Saves a new note. Returns false when the owner already has a note with that title.</summary>
    Task<bool> TryAddAsync(Note note, CancellationToken cancellationToken);

    Task<Note?> FindAsync(Guid id, CancellationToken cancellationToken);

#if (UseAuth)
    /// <summary>The owner's notes, or everyone's when <paramref name="ownerId"/> is null, newest first.</summary>
    Task<PagedResult<Note>> ListAsync(string? ownerId, int page, int pageSize, CancellationToken cancellationToken);
#else
    /// <summary>All notes, newest first.</summary>
    Task<PagedResult<Note>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);
#endif
}
