using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Files;

/// <summary>A file's record. <c>status</c> is <c>pending</c>, <c>available</c> or <c>rejected</c>.</summary>
public sealed record FileResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string Status,
    string? RejectionReason,
    string? Sha256,
    bool HasThumbnail,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt)
{
    internal static FileResponse From(StoredFile file) => new(
        file.Id,
        file.FileName,
        file.ContentType,
        file.Size,
        file.Status.ToString().ToLowerInvariant(),
        file.RejectionReason,
        file.Sha256,
        file.HasThumbnail,
        file.ExpiresAt,
        file.CreatedAt);
}

/// <summary>Asks for a signed link to upload a file straight to storage, without passing through the API.</summary>
public sealed record CreateUploadRequest
{
    [Required]
    [StringLength(StoredFile.FileNameMaxLength)]
    public string FileName { get; init; } = string.Empty;

    /// <summary>The type the upload will send. The PUT must use exactly this Content-Type, and it must be an allowed type.</summary>
    [Required]
    [StringLength(100)]
    public string ContentType { get; init; } = string.Empty;

    /// <summary>The size in bytes. The completed upload must have exactly this size.</summary>
    [Range(1, long.MaxValue)]
    public long Size { get; init; }

    /// <summary>Removed automatically after <c>Files:TemporaryFileHours</c>.</summary>
    public bool Temporary { get; init; }
}

/// <summary>
/// A signed upload: PUT the content to <c>uploadUrl</c> with the given headers before <c>expiresAt</c>,
/// then call <c>POST /v1/files/{id}/complete</c>. Until then <c>file</c> is pending.
/// </summary>
public sealed record UploadTicketResponse(FileResponse File, Uri UploadUrl, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

public sealed record DownloadLinkResponse(Uri Url, DateTimeOffset ExpiresAt);
