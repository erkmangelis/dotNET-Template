using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MyProject.Api.Infrastructure.Caching;

/// <summary>
/// Ready when Redis answers a ping. The API still works without it (reads go to the source), so
/// remove the ready tag if you would rather keep serving while Redis is down.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy($"Ping {latency.TotalMilliseconds:F0} ms");
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Redis does not answer.", exception);
        }
    }
}
