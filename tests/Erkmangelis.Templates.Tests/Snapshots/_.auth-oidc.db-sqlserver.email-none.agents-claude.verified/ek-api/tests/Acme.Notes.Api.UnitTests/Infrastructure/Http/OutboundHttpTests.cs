using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Acme.Notes.Api.UnitTests.Infrastructure.Http;

public sealed class OutboundHttpTests
{
    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("API.Example.com", "api.example.com", true)]
    [InlineData("eu.api.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("evil-example.com", "*.example.com", false)]
    [InlineData("api.example.com.evil.test", "api.example.com", false)]
    public void Hosts_are_matched_exactly_or_as_subdomains(string host, string pattern, bool allowed)
    {
        Assert.Equal(allowed, AllowedHostsHandler.IsAllowed(host, [pattern]));
    }

    [Fact]
    public async Task A_listed_host_is_called()
    {
        var (client, primary) = Build(new OutboundClientOptions { AllowedHosts = ["api.example.com"] });

        using var response = await client.GetAsync(new Uri("https://api.example.com/things"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(primary.Requests);
    }

    [Theory]
    [InlineData("https://metadata.internal/latest")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("http://api.example.com/things")]
    public async Task Other_hosts_and_plain_http_are_refused_before_anything_is_sent(string url)
    {
        var (client, primary) = Build(new OutboundClientOptions { AllowedHosts = ["api.example.com"] });

        await Assert.ThrowsAsync<OutboundRequestRefusedException>(() => client.GetAsync(new Uri(url), TestContext.Current.CancellationToken));
        Assert.Empty(primary.Requests);
    }

    [Fact]
    public async Task Transient_failures_are_retried_for_safe_methods()
    {
        var (client, primary) = Build(new OutboundClientOptions { AllowedHosts = ["api.example.com"], MaxRetries = 2 }, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        using var response = await client.GetAsync(new Uri("https://api.example.com/things"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, primary.Requests.Count);
    }

    [Fact]
    public async Task A_post_is_not_retried_unless_the_client_says_it_is_safe()
    {
        var (client, primary) = Build(new OutboundClientOptions { AllowedHosts = ["api.example.com"], MaxRetries = 2 }, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        using var response = await client.PostAsync(new Uri("https://api.example.com/things"), new StringContent("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(primary.Requests);
    }

    [Fact]
    public void Attempts_must_be_shorter_than_the_whole_call()
    {
        Assert.False(OutboundClientOptions.AreValid(new OutboundClientOptions { TimeoutSeconds = 10, AttemptTimeoutSeconds = 10 }));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    public void Only_public_addresses_count_as_public(string address, bool isPublic)
    {
        Assert.Equal(isPublic, PublicNetwork.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Connecting_to_a_loopback_address_is_refused()
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = PublicNetwork.ConnectAsync };
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://127.0.0.1:9/"), TestContext.Current.CancellationToken));
        Assert.IsType<OutboundRequestRefusedException>(error.InnerException ?? error);
    }

    private static (HttpClient Client, RecordingHandler Primary) Build(OutboundClientOptions options, params HttpStatusCode[] statuses)
    {
        var primary = new RecordingHandler(statuses);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddOutboundClient("test").ConfigurePrimaryHttpMessageHandler(() => primary);
        services.Configure<OutboundClientOptions>("test", configured =>
        {
            configured.AllowedHosts = options.AllowedHosts;
            configured.MaxRetries = options.MaxRetries;
        });
        // Retries in tests should not wait for the real backoff.
        services.Configure<HttpStandardResilienceOptions>("test-standard", resilience => resilience.Retry.Delay = TimeSpan.Zero);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"), primary);
    }

    private sealed class RecordingHandler(HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statuses.Length == 0 ? HttpStatusCode.OK : statuses[Math.Min(Requests.Count, statuses.Length - 1)];
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
