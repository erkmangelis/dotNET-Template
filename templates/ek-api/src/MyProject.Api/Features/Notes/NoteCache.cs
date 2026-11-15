using Microsoft.Extensions.Caching.Hybrid;

namespace MyProject.Api.Features.Notes;

/// <summary>
/// Single notes read through <see cref="HybridCache"/>, for <c>GET /notes/{id}</c> only. Writes read
/// the store directly, so a version check never runs against a cached copy, and remove the entry
/// after they save.
/// </summary>
internal sealed class NoteCache(HybridCache cache)
{
    /// <summary>Every note entry carries this tag, so <c>RemoveByTagAsync</c> can drop them all.</summary>
    public const string Tag = "notes";

    private static readonly string[] _tags = [Tag];

    /// <summary>
    /// The note from the cache, or from <paramref name="store"/> on a miss. Each call gets its own
    /// copy (the cache stores it serialized), so changing it changes nothing in the cache. Callers that
    /// ask for the same id at once share one read.
    /// </summary>
    public ValueTask<Note?> FindAsync(Guid id, INoteStore store, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            Key(id),
            (store, id),
            static async (state, token) => await state.store.FindAsync(state.id, token),
            tags: _tags,
            cancellationToken: cancellationToken);

    /// <summary>Removes the note's entry. Call it after every save that changes, adds or deletes the note.</summary>
    public ValueTask ForgetAsync(Guid id, CancellationToken cancellationToken) => cache.RemoveAsync(Key(id), cancellationToken);

    private static string Key(Guid id) => $"notes:{id:N}";
}
