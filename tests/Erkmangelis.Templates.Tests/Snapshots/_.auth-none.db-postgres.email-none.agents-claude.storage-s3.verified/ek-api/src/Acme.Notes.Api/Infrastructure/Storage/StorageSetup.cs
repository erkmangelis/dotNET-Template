using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Storage;

internal static class StorageSetup
{
    /// <summary>Private object storage on an S3-compatible service, through <see cref="IObjectStorage"/>.</summary>
    public static WebApplicationBuilder AddStorage(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IAmazonS3>(services => CreateClient(services.GetRequiredService<IOptions<StorageOptions>>().Value, publicAddress: false));
        builder.Services.AddSingleton(services =>
        {
            var options = services.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new S3LinkSigner(CreateClient(options, publicAddress: true), new Uri(options.PublicServiceUrl ?? options.ServiceUrl!));
        });
        builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        builder.Services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage", tags: [Observability.ReadyTag]);

        return builder;
    }

    /// <summary>Creates the bucket when <see cref="StorageOptions.CreateBucket"/> asks for it.</summary>
    public static async Task PrepareStorageAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        if (!options.CreateBucket)
        {
            return;
        }

        var s3 = app.Services.GetRequiredService<IAmazonS3>();
        try
        {
            await s3.PutBucketAsync(options.Bucket);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    private static AmazonS3Client CreateClient(StorageOptions options, bool publicAddress)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = publicAddress ? options.PublicServiceUrl ?? options.ServiceUrl : options.ServiceUrl,
            AuthenticationRegion = options.Region,
            ForcePathStyle = options.ForcePathStyle,
            // Checksums only where S3 requires them: not every S3-compatible service accepts the newer default ones.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }
}

/// <summary>Ready only when the bucket answers, so a load balancer never sends uploads to an instance that cannot store them.</summary>
internal sealed class StorageHealthCheck(IAmazonS3 s3, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Value.Bucket, MaxKeys = 1 }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return HealthCheckResult.Unhealthy($"Bucket {options.Value.Bucket}: {exception.ErrorCode}");
        }
        catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException)
        {
            return HealthCheckResult.Unhealthy("The storage service did not answer.", exception);
        }
    }
}
