namespace Acme.Notes.Api.Infrastructure.Storage;

/// <summary>An object in the bucket, as a listing returns it.</summary>
internal sealed record StoredObject(string Key, long Size, DateTimeOffset LastModified);

/// <summary>
/// Private object storage. Nothing in the bucket is public: clients reach an object only through
/// the API, or through a short-lived signed link the API hands out after checking who is asking.
/// </summary>
internal interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>The object's content, or null when there is no such object. Dispose the stream.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>The object's size in bytes, or null when there is no such object.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the object. Removing one that does not exist is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Every object whose key starts with <paramref name="prefix"/>, a page at a time.</summary>
    IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>A link that lets anyone holding it upload this one object with a PUT, with exactly this content type, until it expires.</summary>
    Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan lifetime);

    /// <summary>A link that lets anyone holding it download this one object until it expires, saved as <paramref name="fileName"/>.</summary>
    Task<Uri> CreateDownloadUrlAsync(string key, string fileName, TimeSpan lifetime);
}
