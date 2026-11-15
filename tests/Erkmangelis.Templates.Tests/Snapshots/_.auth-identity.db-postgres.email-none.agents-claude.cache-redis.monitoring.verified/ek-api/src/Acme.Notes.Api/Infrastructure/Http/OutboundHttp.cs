using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Http;

/// <summary>Settings for one outbound HTTP client, bound from <c>OutboundHttp:{client name}</c>.</summary>
internal sealed class OutboundClientOptions
{
    public const string SectionName = "OutboundHttp";

    /// <summary>The address relative request URIs resolve against. Its host is always allowed.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Hosts the client may call besides the ones its code names and <see cref="BaseAddress"/>'s.
    /// <c>*.example.com</c> matches every subdomain of example.com, not example.com itself.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>How long one call may take in all, retries included.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>How long one attempt may take before it is cancelled and, if retries are left, tried again.</summary>
    [Range(1, 300)]
    public int AttemptTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Retries after a transient failure (a 5xx, 408, 429 or a network error), with exponential backoff
    /// and jitter. 0 turns retries off.
    /// </summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retry POST, PUT, PATCH and DELETE too. Leave it off unless the other side drops repeated
    /// requests (an idempotency key, or a webhook id it remembers): a retried POST can do its work twice.
    /// </summary>
    public bool RetryUnsafeMethods { get; set; }

    /// <summary>Allow plain http. For a local service in development only; production calls use https.</summary>
    public bool AllowHttp { get; set; }

    public static bool AreValid(OutboundClientOptions options) => options.AttemptTimeoutSeconds < options.TimeoutSeconds;
}

/// <summary>
/// The one way this app calls other systems over HTTP: a typed or named client from
/// <c>IHttpClientFactory</c> with timeouts, retries and a circuit breaker
/// (<c>Microsoft.Extensions.Http.Resilience</c>), and a list of hosts it may call, so a URL that
/// comes from data can never point it at something else (server-side request forgery).
/// </summary>
internal static class OutboundHttp
{
    /// <summary>
    /// Registers <typeparamref name="TClient"/> as a typed client named <paramref name="name"/>.
    /// <paramref name="allowedHosts"/> are the hosts its code calls; <c>OutboundHttp:{name}:AllowedHosts</c> adds more.
    /// </summary>
    public static IHttpClientBuilder AddOutboundClient<TClient, TImplementation>(this IServiceCollection services, string name, params string[] allowedHosts)
        where TClient : class
        where TImplementation : class, TClient
    {
        AddOptions(services, name);
        return Configure(services.AddHttpClient<TClient, TImplementation>(name, (provider, client) => ConfigureClient(provider, name, client)), name, allowedHosts);
    }

    /// <summary>Registers a named client for <c>IHttpClientFactory.CreateClient(name)</c>, with the same protections.</summary>
    public static IHttpClientBuilder AddOutboundClient(this IServiceCollection services, string name, params string[] allowedHosts)
    {
        AddOptions(services, name);
        return Configure(services.AddHttpClient(name, (provider, client) => ConfigureClient(provider, name, client)), name, allowedHosts);
    }

    private static void AddOptions(IServiceCollection services, string name) =>
        services.AddOptions<OutboundClientOptions>(name)
            .BindConfiguration($"{OutboundClientOptions.SectionName}:{name}")
            .ValidateDataAnnotations()
            .Validate(OutboundClientOptions.AreValid, $"{OutboundClientOptions.SectionName}:{name}:AttemptTimeoutSeconds must be less than TimeoutSeconds.")
            .ValidateOnStart();

    private static void ConfigureClient(IServiceProvider provider, string name, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<OutboundClientOptions>>().Get(name);
        if (options.BaseAddress is not null)
        {
            client.BaseAddress = options.BaseAddress;
        }

        // The resilience handler below owns every timeout.
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    private static IHttpClientBuilder Configure(IHttpClientBuilder builder, string name, string[] allowedHosts)
    {
        // Redirects are followed inside the primary handler, past the host check: never follow them.
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Picks up DNS changes on long-lived pooled connections.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });

        // Outermost, so a refused host is never tried, retried or counted by the circuit breaker.
        builder.AddHttpMessageHandler(provider => new AllowedHostsHandler(name, allowedHosts, provider.GetRequiredService<IOptionsMonitor<OutboundClientOptions>>()));
        builder.AddStandardResilienceHandler().Configure((resilience, provider) =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<OutboundClientOptions>>().Get(name);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds);
            // The breaker must watch at least two attempts' worth of time.
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, 2 * options.AttemptTimeoutSeconds));
            if (options.MaxRetries == 0)
            {
                resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            }
            else
            {
                resilience.Retry.MaxRetryAttempts = options.MaxRetries;
            }

            if (!options.RetryUnsafeMethods)
            {
                resilience.Retry.DisableForUnsafeHttpMethods();
            }
        });

        return builder;
    }
}

/// <summary>Refuses a request whose URI is not https (unless allowed) or whose host is not on the client's list.</summary>
internal sealed class AllowedHostsHandler(string clientName, IReadOnlyCollection<string> builtInHosts, IOptionsMonitor<OutboundClientOptions> options)
    : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var settings = options.Get(clientName);
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            throw new OutboundRequestRefusedException($"The {clientName} client needs an absolute URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(settings.AllowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new OutboundRequestRefusedException($"The {clientName} client only calls https URIs, not {uri.Scheme}.");
        }

        IEnumerable<string> allowed = [.. builtInHosts, .. settings.AllowedHosts];
        if (settings.BaseAddress is not null)
        {
            allowed = allowed.Append(settings.BaseAddress.IdnHost);
        }

        if (!IsAllowed(uri.IdnHost, allowed))
        {
            throw new OutboundRequestRefusedException($"The {clientName} client may not call {uri.IdnHost}. Add it to {OutboundClientOptions.SectionName}:{clientName}:AllowedHosts if it should.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>True when <paramref name="host"/> is one of <paramref name="patterns"/>, or a subdomain of a <c>*.</c> pattern.</summary>
    public static bool IsAllowed(string host, IEnumerable<string> patterns) => patterns.Any(pattern =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An outbound request this app refused to send, because of where it pointed.</summary>
internal sealed class OutboundRequestRefusedException(string message) : HttpRequestException(message);
