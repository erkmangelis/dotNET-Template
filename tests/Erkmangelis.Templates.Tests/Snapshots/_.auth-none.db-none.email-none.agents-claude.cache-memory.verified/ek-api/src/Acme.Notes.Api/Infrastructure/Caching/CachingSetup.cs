using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Caching;

/// <summary>Output cache policy names, for <c>CacheOutput(CachePolicies.X)</c>.</summary>
internal static class CachePolicies
{
    /// <summary>A response that is the same for every caller, kept for <see cref="CachingOptions.OutputExpirationSeconds"/>.</summary>
    public const string Shared = "shared";
}

internal static class CachingSetup
{
    /// <summary>
    /// <see cref="HybridCache"/> in memory, and output caching for responses that are the same for everyone.
    /// </summary>
    public static WebApplicationBuilder AddCaching(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<CachingOptions>()
            .BindConfiguration(CachingOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => options.LocalExpirationSeconds <= options.ExpirationSeconds, "Caching:LocalExpirationSeconds must not exceed Caching:ExpirationSeconds.")
            .ValidateOnStart();

        services.AddHybridCache();
        services.AddOptions<HybridCacheOptions>().Configure<IOptions<CachingOptions>>((hybrid, caching) =>
        {
            hybrid.MaximumPayloadBytes = caching.Value.MaximumPayloadBytes;
            hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(caching.Value.ExpirationSeconds),
                LocalCacheExpiration = TimeSpan.FromSeconds(caching.Value.LocalExpirationSeconds),
            };
        });

        // Output caching keeps whole responses. Its default rules already skip requests that carry
        // credentials and responses that set cookies; it applies only where an endpoint asks for it.
        services.AddOutputCache();
        services.AddOptions<OutputCacheOptions>().Configure<IOptions<CachingOptions>>((output, caching) =>
            output.AddPolicy(CachePolicies.Shared, policy => policy.Expire(TimeSpan.FromSeconds(caching.Value.OutputExpirationSeconds))));

        return builder;
    }
}
