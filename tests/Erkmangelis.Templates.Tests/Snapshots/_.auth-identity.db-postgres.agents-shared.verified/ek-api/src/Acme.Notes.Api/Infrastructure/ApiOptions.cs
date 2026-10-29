using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Infrastructure;

/// <summary>General API settings, bound from the <c>Api</c> section.</summary>
internal sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Serve the OpenAPI document and the Scalar reference UI.</summary>
    public bool ExposeOpenApi { get; set; }

    /// <summary>Redirect plain HTTP to HTTPS. Turn off only when TLS ends at a proxy that never forwards plain HTTP.</summary>
    public bool UseHttpsRedirection { get; set; } = true;

    /// <summary>How long in-flight requests get to finish after a stop signal.</summary>
    [Range(1, 300)]
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}

/// <summary>Cross-origin settings, bound from the <c>Cors</c> section. No origins means no cross-origin access.</summary>
internal sealed class CorsSettings
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];

    internal static bool AreValid(CorsSettings settings) =>
        settings.AllowedOrigins.All(origin =>
            origin != "*"
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http"
            && uri.AbsolutePath == "/");
}

/// <summary>Rate limiting settings, bound from the <c>RateLimiting</c> section.</summary>
internal sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests allowed per caller in each window. Callers are signed-in users, or IP addresses otherwise.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 100;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Which proxies may set X-Forwarded-For and X-Forwarded-Proto, bound from the <c>ForwardedHeaders</c> section.</summary>
internal sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Proxy networks in CIDR form, for example <c>172.16.0.0/12</c> for a Docker network.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Single proxy addresses, for example <c>10.0.0.5</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    [Range(1, 10)]
    public int ForwardLimit { get; set; } = 1;

    internal static bool AreValid(ForwardedHeadersSettings settings) =>
        settings.KnownNetworks.All(network => System.Net.IPNetwork.TryParse(network, out _))
        && settings.KnownProxies.All(proxy => System.Net.IPAddress.TryParse(proxy, out _));
}
