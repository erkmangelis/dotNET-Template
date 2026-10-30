namespace Acme.Notes.Api.Features.Files;

internal enum FileStatus
{
    /// <summary>Waiting for its content: a signed upload or a tus upload that has not finished.</summary>
    Pending,

    /// <summary>Checked and stored. Only available files can be downloaded.</summary>
    Available,

    /// <summary>Refused: wrong type, too large, or the scanner found something. The content is gone; the record says why.</summary>
    Rejected,
}

/// <summary>
/// A file's record: who owns it, what it is and where its content lives. The content itself is in
/// object storage under <see cref="ObjectKey"/>; this row is what the API checks before handing it out.
/// </summary>
internal sealed class StoredFile : IRecordMetadata, IAudited
{
    public const int FileNameMaxLength = 255;

    public Guid Id { get; init; }

    /// <summary>The signed-in user who uploaded the file. Only they can read it.</summary>
    public required string OwnerId { get; init; }

    /// <summary>The name the client gave, used only when the file is saved. It never becomes part of a storage key.</summary>
    public required string FileName { get; set; }

    /// <summary>The detected type once the file is checked; until then, the type the client declared.</summary>
    public required string ContentType { get; set; }

    public long Size { get; set; }

    /// <summary>The SHA-256 of the content, in lower-case hex, once the file is checked.</summary>
    public string? Sha256 { get; set; }

    public FileStatus Status { get; set; }

    public string? RejectionReason { get; set; }

    public bool HasThumbnail { get; set; }

    /// <summary>Uploaded as temporary: once available, it expires after <c>Files:TemporaryFileHours</c>.</summary>
    public bool IsTemporary { get; set; }

    /// <summary>When cleanup removes the file: for temporary, pending and rejected files. Null keeps it until it is deleted.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }

    public string ObjectKey => ObjectKeys.Content(Id);

    public string ThumbnailKey => ObjectKeys.Thumbnail(Id);
}

/// <summary>Storage keys are built from the file's id alone, so no client input ever names an object.</summary>
internal static class ObjectKeys
{
    public const string ContentPrefix = "files/";
    public const string ThumbnailPrefix = "thumbnails/";

    public static string Content(Guid id) => $"{ContentPrefix}{id:N}";

    public static string Thumbnail(Guid id) => $"{ThumbnailPrefix}{id:N}.webp";

    /// <summary>The file id a key belongs to, or null for a key this app did not make.</summary>
    public static Guid? FileId(string key)
    {
        var name = key.StartsWith(ContentPrefix, StringComparison.Ordinal) ? key[ContentPrefix.Length..]
            : key.StartsWith(ThumbnailPrefix, StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(key[ThumbnailPrefix.Length..])
            : null;
        return Guid.TryParseExact(name, "N", out var id) ? id : null;
    }
}
