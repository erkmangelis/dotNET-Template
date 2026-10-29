using System.ComponentModel.DataAnnotations;

namespace MyProject.Api.Features.Notes;

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

public sealed record NoteResponse(Guid Id, string Title, string? Content, DateTimeOffset CreatedAt)
{
    internal static NoteResponse From(Note note) => new(note.Id, note.Title, note.Content, note.CreatedAt);
}
