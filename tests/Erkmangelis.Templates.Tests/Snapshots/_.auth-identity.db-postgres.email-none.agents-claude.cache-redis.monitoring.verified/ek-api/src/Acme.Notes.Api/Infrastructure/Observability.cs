using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Acme.Notes.Api.Infrastructure;

internal static class Observability
{
    public const string ReadyTag = "ready";

    /// <summary>Where the Prometheus scrape endpoint listens when <c>Monitoring:Prometheus:Enabled</c> is on.</summary>
    public const string PrometheusPath = "/metrics";

    /// <summary>
    /// Structured logs with trace ids, OpenTelemetry traces and metrics, and health checks.
    /// Telemetry is exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// With <c>Monitoring:Prometheus:Enabled</c>, metrics are served for Prometheus to scrape instead
    /// of pushed over OTLP; traces and logs still go over OTLP. See docs/monitoring.md.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        var prometheus = builder.Configuration.GetValue<bool>("Monitoring:Prometheus:Enabled");
        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "O";
            });
        }

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health") && !context.Request.Path.StartsWithSegments(PrometheusPath))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql")
            )
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                metrics.AddMeter("Npgsql");
                if (prometheus)
                {
                    metrics.AddPrometheusExporter();
                }
            })
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            if (prometheus)
            {
                // Metrics are scraped, so only traces and logs are pushed; pushing metrics too would count them twice.
                telemetry.WithTracing(tracing => tracing.AddOtlpExporter()).WithLogging(logging => logging.AddOtlpExporter());
            }
            else
            {
                telemetry.UseOtlpExporter();
            }
        }

        builder.Services.AddHealthChecks();

        return builder;
    }

    /// <summary>
    /// <c>/health/live</c> says the process is up. <c>/health/ready</c> also runs the checks tagged
    /// <see cref="ReadyTag"/>, such as the database, and is what load balancers and compose should probe.
    /// </summary>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        var health = app.MapGroup("/health").DisableRateLimiting().ExcludeFromDescription();
        health.MapHealthChecks("/live", new HealthCheckOptions { Predicate = _ => false });
        health.MapHealthChecks("/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

        if (app.Configuration.GetValue<bool>("Monitoring:Prometheus:Enabled"))
        {
            // Only on the separate port, which compose keeps inside its network: metrics name every
            // route and are not for the public. Add the port to ASPNETCORE_HTTP_PORTS (see compose.yaml).
            var port = app.Configuration.GetValue("Monitoring:Prometheus:Port", 9464);
            app.MapPrometheusScrapingEndpoint(PrometheusPath)
                .RequireHost($"*:{port}")
                .DisableRateLimiting()
                .ExcludeFromDescription();
        }

        return app;
    }
}
