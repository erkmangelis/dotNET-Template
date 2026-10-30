using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Infrastructure.Storage;

/// <summary>
/// Object storage settings, bound from the <c>Storage</c> section. Any S3-compatible service works:
/// RustFS in compose, Cloudflare R2, Amazon S3, Backblaze B2 and others.
/// </summary>
internal sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>The address the API uses, such as <c>http://storage:9000</c> in compose or <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>.</summary>
    [Required(ErrorMessage = "Set Storage:ServiceUrl to the S3 endpoint.")]
    [Url]
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// The address clients use for signed links, when it differs from <see cref="ServiceUrl"/>: in
    /// compose the API reaches the storage by its service name, a browser by the published port.
    /// </summary>
    [Url]
    public string? PublicServiceUrl { get; set; }

    /// <summary><c>auto</c> for Cloudflare R2; any region for RustFS.</summary>
    [Required]
    public string Region { get; set; } = "us-east-1";

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", ErrorMessage = "Storage:Bucket must be a valid bucket name.")]
    public string Bucket { get; set; } = "files";

    /// <summary>Set it with user secrets in development and an environment variable everywhere else.</summary>
    [Required(ErrorMessage = "Set Storage:AccessKey with user secrets or the Storage__AccessKey environment variable.")]
    public string? AccessKey { get; set; }

    [Required(ErrorMessage = "Set Storage:SecretKey with user secrets or the Storage__SecretKey environment variable.")]
    public string? SecretKey { get; set; }

    /// <summary>Bucket in the path (<c>host/bucket/key</c>), as RustFS and R2 expect. Amazon S3 also accepts it.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Create the bucket at startup when it is missing. Meant for development and tests; create production buckets yourself.</summary>
    public bool CreateBucket { get; set; }
}
