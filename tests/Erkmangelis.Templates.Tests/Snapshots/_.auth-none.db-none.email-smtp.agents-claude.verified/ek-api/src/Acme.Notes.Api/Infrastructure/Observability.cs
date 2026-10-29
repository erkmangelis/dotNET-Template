using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Acme.Notes.Api.Infrastructure;

internal static class Observability
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// Structured logs with trace ids, OpenTelemetry traces and metrics, and health checks.
    /// Telemetry is exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
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
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
            )
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
            )
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
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

        return app;
    }
}
