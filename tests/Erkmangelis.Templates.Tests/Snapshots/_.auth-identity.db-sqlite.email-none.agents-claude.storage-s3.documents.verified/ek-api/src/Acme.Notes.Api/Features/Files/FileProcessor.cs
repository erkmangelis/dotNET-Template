using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Files;

internal enum Rejection
{
    None,
    TooLarge,
    Empty,
    TypeNotAllowed,
    Infected,
    ImageTooLarge,
    SizeMismatch,
}

/// <summary>
/// The one path every upload takes, however it arrived (through the API, a signed link or tus): the
/// size, the real type from the file's signature, a virus scan, a hash and, for images, a thumbnail.
/// Only then is the file available. A refused file's content is removed.
/// </summary>
internal sealed partial class FileProcessor(
    IObjectStorage storage,
    IVirusScanner scanner,
    IOptions<FilesOptions> options,
    TimeProvider time,
    ILogger<FileProcessor> logger)
{
    /// <summary>
    /// Checks <paramref name="content"/> and updates <paramref name="file"/> to match; the caller saves it.
    /// The content goes to storage unless <paramref name="alreadyStored"/>, in which case a refused file's object is deleted.
    /// <paramref name="content"/> must be seekable: it is read several times.
    /// </summary>
    public async Task<Rejection> ProcessAsync(StoredFile file, Stream content, bool alreadyStored, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var (rejection, reason) = await InspectAsync(file, content, settings, cancellationToken);
        if (rejection != Rejection.None)
        {
            return await RefuseAsync(file, rejection, reason!, alreadyStored, cancellationToken);
        }

        if (!alreadyStored)
        {
            content.Position = 0;
            await storage.PutAsync(file.ObjectKey, content, file.ContentType, cancellationToken);
        }

        if (FileSignatures.IsImage(file.ContentType))
        {
            content.Position = 0;
            if (ImageThumbnails.Create(content, settings.ThumbnailSize) is { } thumbnail)
            {
                using var thumbnailContent = new MemoryStream(thumbnail);
                await storage.PutAsync(file.ThumbnailKey, thumbnailContent, ImageThumbnails.ContentType, cancellationToken);
                file.HasThumbnail = true;
            }
        }

        file.Status = FileStatus.Available;
        file.RejectionReason = null;
        file.ExpiresAt = file.IsTemporary ? time.GetUtcNow().AddHours(settings.TemporaryFileHours) : null;
        return Rejection.None;
    }

    /// <summary>Marks the file refused, to be removed after <c>Files:RejectedFileHours</c>, and deletes its content if it was stored.</summary>
    public async Task<Rejection> RefuseAsync(StoredFile file, Rejection rejection, string reason, bool alreadyStored, CancellationToken cancellationToken)
    {
        if (alreadyStored)
        {
            await storage.DeleteAsync(file.ObjectKey, cancellationToken);
        }

        file.Status = FileStatus.Rejected;
        file.RejectionReason = reason;
        file.ExpiresAt = time.GetUtcNow().AddHours(options.Value.RejectedFileHours);
        LogRejected(logger, file.Id, rejection);
        return rejection;
    }

    private async Task<(Rejection Rejection, string? Reason)> InspectAsync(StoredFile file, Stream content, FilesOptions settings, CancellationToken cancellationToken)
    {
        var size = content.Length;
        if (size > settings.MaxSizeBytes)
        {
            return (Rejection.TooLarge, $"The file is larger than {settings.MaxSizeBytes} bytes.");
        }

        if (size == 0)
        {
            return (Rejection.Empty, "The file is empty.");
        }

        // A signed upload declared its size up front; storing something else breaks that promise.
        if (file.Status == FileStatus.Pending && file.Size > 0 && file.Size != size)
        {
            return (Rejection.SizeMismatch, $"The upload has {size} bytes, not the {file.Size} declared.");
        }

        var header = new byte[(int)Math.Min(FileSignatures.HeaderLength, size)];
        content.Position = 0;
        await content.ReadExactlyAsync(header, cancellationToken);
        var contentType = FileSignatures.Detect(header, file.FileName);
        if (contentType is null || !settings.AllowedTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return (Rejection.TypeNotAllowed, $"Files of this type are not accepted. Accepted: {string.Join(", ", settings.AllowedTypes)}.");
        }

        if (FileSignatures.IsImage(contentType))
        {
            content.Position = 0;
            if (ImageThumbnails.PixelCount(content) is not { } pixels || pixels > settings.MaxImagePixels)
            {
                return (Rejection.ImageTooLarge, $"The image cannot be read, or has more than {settings.MaxImagePixels} pixels.");
            }
        }

        content.Position = 0;
        var scan = await scanner.ScanAsync(content, cancellationToken);
        if (!scan.IsClean)
        {
            LogInfected(logger, file.Id, scan.Threat!);
            return (Rejection.Infected, "The virus scanner refused the file.");
        }

        content.Position = 0;
        file.Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
        file.ContentType = contentType;
        file.Size = size;
        return (Rejection.None, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "File {FileId} refused: {Rejection}")]
    private static partial void LogRejected(ILogger logger, Guid fileId, Rejection rejection);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File {FileId} refused by the virus scanner: {Threat}")]
    private static partial void LogInfected(ILogger logger, Guid fileId, string threat);
}
