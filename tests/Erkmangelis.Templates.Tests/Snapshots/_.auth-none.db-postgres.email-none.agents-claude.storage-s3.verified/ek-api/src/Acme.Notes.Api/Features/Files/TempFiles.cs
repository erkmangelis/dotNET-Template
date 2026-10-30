namespace Acme.Notes.Api.Features.Files;

/// <summary>Seekable copies of uploads on local disk, removed when the stream is disposed.</summary>
internal static class TempFiles
{
    public static FileStream Create() => new(
        Path.Combine(Path.GetTempPath(), $"upload-{Guid.NewGuid():N}"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    /// <summary>A local copy of an object, at position 0, for <see cref="FileProcessor"/> to read as often as it needs.</summary>
    public static async Task<FileStream> DownloadAsync(IObjectStorage storage, string key, CancellationToken cancellationToken)
    {
        var copy = Create();
        try
        {
            await using (var content = await storage.OpenReadAsync(key, cancellationToken) ?? throw new FileNotFoundException("The object is gone.", key))
            {
                await content.CopyToAsync(copy, cancellationToken);
            }

            copy.Position = 0;
            return copy;
        }
        catch
        {
            await copy.DisposeAsync();
            throw;
        }
    }
}
