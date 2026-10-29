namespace MyProject.Api.Features.Notes;

/// <summary>The example feature: a short text note.</summary>
#if (UsePostgres)
internal sealed class Note : IRecordMetadata, ISoftDeletable, IAudited
#else
internal sealed class Note : IRecordMetadata, ISoftDeletable
#endif
{
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 10_000;

    public Guid Id { get; init; }

#if (UseAuth)
    /// <summary>The signed-in user who created the note. Only they can read it.</summary>
    public required string OwnerId { get; init; }

#endif
    /// <summary>Unique per owner.</summary>
    public required string Title { get; set; }

    public string? Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
#if (UseAuth)

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }
#endif

    public DateTimeOffset? DeletedAt { get; set; }

#if (UsePostgres)
    /// <summary>Changes on every save: PostgreSQL's <c>xmin</c> row version. A save that expects an older one fails.</summary>
#else
    /// <summary>Changes on every save. A save that expects an older one fails.</summary>
#endif
    public uint Version { get; set; }
#if (UseAuth)

    public string? DeletedBy { get; set; }
#endif

    /// <summary>A copy, so a caller can change it without touching the stored one.</summary>
    public Note Clone() => (Note)MemberwiseClone();
}
