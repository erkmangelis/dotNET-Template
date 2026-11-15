using System.ComponentModel.DataAnnotations;

namespace MyProject.Api.Infrastructure.Caching;

/// <summary>Cache lifetimes and limits, bound from the <c>Caching</c> section.</summary>
internal sealed class CachingOptions
{
    public const string SectionName = "Caching";

#if (UseRedis)
    /// <summary>The Redis (or Valkey) connection, from <c>ConnectionStrings:Cache</c>.</summary>
    [Required]
    public string? ConnectionString { get; set; }

    /// <summary>
    /// How long an entry lives in Redis, shared by every instance. A change removes it at once
    /// (see <see cref="LocalExpirationSeconds"/> for what other instances may still hold).
    /// </summary>
#else
    /// <summary>How long an entry lives. A change on this instance removes it at once.</summary>
#endif
    [Range(1, 86_400)]
    public int ExpirationSeconds { get; set; } = 300;

#if (UseRedis)
    /// <summary>
    /// How long an entry also lives in each instance's memory. A change removes the entry from Redis
    /// and from the instance that made it; other instances can serve their copy until this runs out,
    /// so keep it short.
    /// </summary>
#else
    /// <summary>How long an entry stays in memory, at most <see cref="ExpirationSeconds"/>.</summary>
#endif
    [Range(1, 86_400)]
    public int LocalExpirationSeconds { get; set; } = 30;

    /// <summary>The largest value the cache stores; bigger ones are read from the source every time.</summary>
    [Range(1_024, 64 * 1024 * 1024)]
    public int MaximumPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>How long output caching keeps a response (the OpenAPI document, for example).</summary>
    [Range(1, 86_400)]
    public int OutputExpirationSeconds { get; set; } = 60;
}
