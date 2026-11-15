using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class MonitoringTests(ApiFactory api)
{
    [Fact]
    public async Task Prometheus_scrapes_metrics_on_the_metrics_port_only()
    {
        await using var factory = api.WithWebHostBuilder(builder => builder
            .UseSetting("Monitoring:Prometheus:Enabled", "true")
            .UseSetting("Monitoring:Prometheus:Port", "9464"));
        using var client = factory.CreateClient();
        await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        await client.GetAsync($"/v1/notes/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        var onApiPort = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        using var scraper = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:9464") });
        var onMetricsPort = await scraper.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, onApiPort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onMetricsPort.StatusCode);
        Assert.Contains("http_server_request_duration_seconds_bucket", await onMetricsPort.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task There_is_no_metrics_endpoint_unless_it_is_turned_on()
    {
        using var scraper = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:9464") });

        var response = await scraper.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
