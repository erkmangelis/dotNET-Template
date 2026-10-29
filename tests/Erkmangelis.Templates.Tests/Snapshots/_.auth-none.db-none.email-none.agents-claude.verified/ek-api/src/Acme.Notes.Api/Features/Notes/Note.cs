namespace Acme.Notes.Api.Features.Notes;

/// <summary>The example feature: a short text note.</summary>
internal sealed class Note : IRecordMetadata
{
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 10_000;

    public Guid Id { get; init; }

    /// <summary>Unique per owner.</summary>
    public required string Title { get; set; }

    public string? Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A copy, so a caller can change it without touching the stored one.</summary>
    public Note Clone() => (Note)MemberwiseClone();
}
