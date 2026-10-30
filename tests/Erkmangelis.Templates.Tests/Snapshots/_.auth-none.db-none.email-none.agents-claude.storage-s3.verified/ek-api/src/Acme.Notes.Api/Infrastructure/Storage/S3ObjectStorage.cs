using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Acme.Notes.Api.Infrastructure.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> on any S3-compatible service. <paramref name="links"/> signs links
/// for clients and never sends a request: it carries the public address, which the API itself may
/// not be able to reach.
/// </summary>
internal sealed class S3ObjectStorage(IAmazonS3 s3, S3LinkSigner links, IOptions<StorageOptions> options, TimeProvider time) : IObjectStorage
{
    private string Bucket => options.Value.Bucket;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            },
            cancellationToken);

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3.GetObjectAsync(Bucket, key, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(Bucket, key, cancellationToken);
            return metadata.ContentLength;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await s3.DeleteObjectAsync(Bucket, key, cancellationToken);

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await s3.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                yield return new StoredObject(item.Key, item.Size ?? 0, item.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime()) : DateTimeOffset.MinValue);
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    public Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan lifetime) =>
        links.SignAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = time.GetUtcNow().Add(lifetime).UtcDateTime,
        });

    public Task<Uri> CreateDownloadUrlAsync(string key, string fileName, TimeSpan lifetime)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = time.GetUtcNow().Add(lifetime).UtcDateTime,
        };
        // Saved under its own name, never shown inline: the browser must not render an uploaded file on the storage's origin.
        request.ResponseHeaderOverrides.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        return links.SignAsync(request);
    }
}

/// <summary>Signs links with a client for the address clients use. Signing happens locally; this client sends nothing.</summary>
internal sealed class S3LinkSigner(IAmazonS3 signer, Uri publicUrl)
{
    public async Task<Uri> SignAsync(GetPreSignedUrlRequest request)
    {
        request.Protocol = publicUrl.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        return new Uri(await signer.GetPreSignedURLAsync(request));
    }
}
