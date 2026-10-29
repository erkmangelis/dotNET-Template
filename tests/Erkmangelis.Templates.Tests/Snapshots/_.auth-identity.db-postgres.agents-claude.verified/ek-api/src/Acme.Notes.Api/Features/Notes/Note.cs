namespace Acme.Notes.Api.Features.Notes;

/// <summary>The example feature: a short text note.</summary>
internal sealed class Note
{
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 10_000;

    public Guid Id { get; init; }

    /// <summary>The signed-in user who created the note. Only they can read it.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Unique per owner.</summary>
    public required string Title { get; init; }

    public string? Content { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
