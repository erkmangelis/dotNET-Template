using System.ComponentModel.DataAnnotations;

namespace MyProject.Api.Features.Notes;

/// <summary>
/// What a client may send. Only these fields are bound, so a client cannot set the id,
/// the record metadata or the owner.
/// </summary>
public sealed record CreateNoteRequest
{
    [Required]
    [StringLength(Note.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;

    [StringLength(Note.ContentMaxLength)]
    public string? Content { get; init; }
}

/// <summary>
/// Which notes to return and in what order, bound from the query string. Sorting is limited to
/// <see cref="Sorts"/>, and every sort ends with the id so the order is stable.
/// </summary>
public record NoteFilter : IValidatableObject
{
    public const string DefaultSort = "-createdAt";

    internal static readonly SortFields<Note> Sorts = new SortFields<Note>()
        .Add("createdAt", note => note.CreatedAt)
        .Add("updatedAt", note => note.UpdatedAt)
        .Add("title", note => note.Title);

    /// <summary>Only notes whose title contains this text, ignoring case.</summary>
    [StringLength(Note.TitleMaxLength)]
    public string? Search { get; init; }

    /// <summary>Only notes created at or after this time.</summary>
    public DateTimeOffset? CreatedFrom { get; init; }

    /// <summary>Only notes created before this time.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>Comma-separated fields, each optionally prefixed with <c>-</c> for descending. Newest first by default.</summary>
    public string? Sort { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Sort is not null && !Sorts.IsValid(Sort))
        {
            yield return new ValidationResult(
                $"Sort by up to {SortFields<Note>.MaxTerms} of {string.Join(", ", Sorts.Names)}, each optionally prefixed with '-' for descending.",
                [nameof(Sort)]);
        }
    }

    /// <summary>Applies the time filters. Stores apply <see cref="Search"/> themselves, because matching text differs by database.</summary>
    internal IQueryable<Note> Filter(IQueryable<Note> notes)
    {
        if (CreatedFrom is { } from)
        {
            var utc = from.ToUniversalTime();
            notes = notes.Where(note => note.CreatedAt >= utc);
        }

        if (CreatedBefore is { } before)
        {
            var utc = before.ToUniversalTime();
            notes = notes.Where(note => note.CreatedAt < utc);
        }

        return notes;
    }

    internal IQueryable<Note> Order(IQueryable<Note> notes) =>
        Sorts.Apply(notes, Sort ?? DefaultSort).ThenByDescending(note => note.Id);
}

/// <summary>A <see cref="NoteFilter"/> plus the page to return.</summary>
public sealed record NoteListQuery : NoteFilter
{
    /// <summary>The page to return, from 1. Nullable only so the query string may leave it out.</summary>
    [Range(1, Paging.MaxPage)]
    public int? Page { get; init; }

    [Range(1, Paging.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int PageNumber => Page ?? 1;

    internal int PageLength => PageSize ?? Paging.DefaultPageSize;
}

/// <summary>Replaces a note's title and content.</summary>
public sealed record UpdateNoteRequest
{
    /// <summary>
    /// The <c>version</c> you read. When it is no longer current, someone else changed the note and
    /// the update answers 409 instead of overwriting their change.
    /// </summary>
#if (UseSqlServer || UseSqlite)
    public ulong? Version { get; init; }
#else
    public uint? Version { get; init; }
#endif

    [Required]
    [StringLength(Note.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;

    [StringLength(Note.ContentMaxLength)]
    public string? Content { get; init; }
}

/// <summary>
/// A note. <c>Version</c> changes on every save, and the <c>ETag</c> response header carries it: send
/// either back with an update so it cannot overwrite someone else's change.
/// </summary>
#if (UseSqlServer || UseSqlite)
public sealed record NoteResponse(Guid Id, string Title, string? Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ulong Version)
#else
public sealed record NoteResponse(Guid Id, string Title, string? Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version)
#endif
{
    internal static NoteResponse From(Note note) => new(note.Id, note.Title, note.Content, note.CreatedAt, note.UpdatedAt, note.Version);
}
#if (UseAuth)

/// <summary>A note as an admin sees it, with its owner and who changed it last.</summary>
public sealed record AdminNoteResponse(
    Guid Id,
    string OwnerId,
    string Title,
    string? Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy)
{
    internal static AdminNoteResponse From(Note note) =>
        new(note.Id, note.OwnerId, note.Title, note.Content, note.CreatedAt, note.UpdatedAt, note.UpdatedBy);
}
#endif
