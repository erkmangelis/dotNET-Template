using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Notes;

/// <summary>
/// What a client may send. Only these fields are bound, so a client cannot set the id,
/// the creation time or the owner.
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
/// Filters, sort and page for listing notes, bound from the query string. Sorting is limited to
/// <see cref="Sorts"/>, and every sort ends with the id so pages never overlap.
/// </summary>
public sealed record NoteListQuery : IValidatableObject
{
    public const string DefaultSort = "-createdAt";

    internal static readonly SortFields<Note> Sorts = new SortFields<Note>()
        .Add("createdAt", note => note.CreatedAt)
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

    /// <summary>The page to return, from 1. Nullable only so the query string may leave it out.</summary>
    [Range(1, Paging.MaxPage)]
    public int? Page { get; init; }

    [Range(1, Paging.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int PageNumber => Page ?? 1;

    internal int PageLength => PageSize ?? Paging.DefaultPageSize;

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

public sealed record NoteResponse(Guid Id, string Title, string? Content, DateTimeOffset CreatedAt)
{
    internal static NoteResponse From(Note note) => new(note.Id, note.Title, note.Content, note.CreatedAt);
}
