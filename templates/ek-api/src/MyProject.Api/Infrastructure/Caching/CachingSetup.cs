using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Hybrid;
#if (UseRedis)
using Microsoft.Extensions.Caching.StackExchangeRedis;
#endif
using Microsoft.Extensions.Options;
#if (UseRedis)
using StackExchange.Redis;
#endif

namespace MyProject.Api.Infrastructure.Caching;

/// <summary>Output cache policy names, for <c>CacheOutput(CachePolicies.X)</c>.</summary>
internal static class CachePolicies
{
    /// <summary>A response that is the same for every caller, kept for <see cref="CachingOptions.OutputExpirationSeconds"/>.</summary>
    public const string Shared = "shared";
}

internal static class CachingSetup
{
    /// <summary>
#if (UseRedis)
    /// <see cref="HybridCache"/> in memory with Redis as the shared second level, and output caching
    /// for responses that are the same for everyone.
#else
    /// <see cref="HybridCache"/> in memory, and output caching for responses that are the same for everyone.
#endif
    /// </summary>
    public static WebApplicationBuilder AddCaching(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<CachingOptions>()
            .BindConfiguration(CachingOptions.SectionName)
#if (UseRedis)
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString ??= configuration.GetConnectionString("Cache"))
#endif
            .ValidateDataAnnotations()
            .Validate(options => options.LocalExpirationSeconds <= options.ExpirationSeconds, "Caching:LocalExpirationSeconds must not exceed Caching:ExpirationSeconds.")
            .ValidateOnStart();

#if (UseRedis)
        // One connection for the process, made on first use. It reconnects by itself; a Redis that is
        // down makes cache reads fall back to the source instead of failing requests.
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var configuration = ConfigurationOptions.Parse(provider.GetRequiredService<IOptions<CachingOptions>>().Value.ConnectionString!);
            configuration.AbortOnConnectFail = false;
            configuration.ClientName = builder.Environment.ApplicationName;
            return ConnectionMultiplexer.Connect(configuration);
        });
        // HybridCache uses the registered IDistributedCache as its second level.
        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((redis, provider) =>
        {
            redis.ConnectionMultiplexerFactory = () => Task.FromResult(provider.GetRequiredService<IConnectionMultiplexer>());
            // Several apps can share one Redis without reading each other's keys.
            redis.InstanceName = $"{builder.Environment.ApplicationName}:";
        });
        services.AddHealthChecks().AddCheck<RedisHealthCheck>("cache", tags: [Observability.ReadyTag]);

#endif
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
