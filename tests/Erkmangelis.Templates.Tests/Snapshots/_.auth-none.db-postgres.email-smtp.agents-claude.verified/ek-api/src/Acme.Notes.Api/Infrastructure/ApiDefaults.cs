using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace Acme.Notes.Api.Infrastructure;

internal static class ApiDefaults
{
    /// <summary>Registers the services every endpoint relies on.</summary>
    public static WebApplicationBuilder AddApiDefaults(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

        services.AddOptions<ApiOptions>()
            .BindConfiguration(ApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<CorsSettings>()
            .BindConfiguration(CorsSettings.SectionName)
            .Validate(CorsSettings.AreValid, "Cors:AllowedOrigins must hold absolute http(s) origins without paths, and never '*'.")
            .ValidateOnStart();
        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ForwardedHeadersSettings>()
            .BindConfiguration(ForwardedHeadersSettings.SectionName)
            .ValidateDataAnnotations()
            .Validate(ForwardedHeadersSettings.AreValid, "ForwardedHeaders:KnownNetworks must be CIDR ranges and KnownProxies must be IP addresses.")
            .ValidateOnStart();

        services.AddOptions<HostOptions>()
            .Configure<IOptions<ApiOptions>>((host, api) =>
                host.ShutdownTimeout = TimeSpan.FromSeconds(api.Value.ShutdownTimeoutSeconds));

        services.TryAddSingleton(TimeProvider.System);

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
            context.ProblemDetails.Extensions.TryAdd(
                "traceId",
                Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
        });
        services.AddValidation();
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Acme.Notes API";
            document.Info.Version = "v1";
            return Task.CompletedTask;
        }));

        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IOptions<CorsSettings>>((cors, settings) => cors.AddDefaultPolicy(policy =>
            {
                if (settings.Value.AllowedOrigins.Length > 0)
                {
                    policy.WithOrigins(settings.Value.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
                    if (settings.Value.AllowCredentials)
                    {
                        policy.AllowCredentials();
                    }
                }
            }));

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((forwarded, settings) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = settings.Value.ForwardLimit;
                foreach (var network in settings.Value.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }

                foreach (var proxy in settings.Value.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(PartitionByCaller);
            options.OnRejected = WriteRateLimitProblemAsync;
        });

        return builder;
    }

    /// <summary>Adds the middleware in the order the services above expect.</summary>
    public static WebApplication UseApiDefaults(this WebApplication app)
    {
        var api = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;

        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        if (api.UseHttpsRedirection)
        {
            app.UseHttpsRedirection();
        }

        app.UseSecurityHeaders();
        app.UseCors();
        app.UseRateLimiter();

        if (api.ExposeOpenApi)
        {
            app.MapOpenApi().DisableRateLimiting();
            app.MapScalarApiReference().DisableRateLimiting();
        }

        return app;
    }

    private static RateLimitPartition<string> PartitionByCaller(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitingOptions>>().CurrentValue;
        if (!options.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var key = userId is not null ? $"user:{userId}" : $"ip:{context.Connection.RemoteIpAddress}";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit,
            Window = TimeSpan.FromSeconds(options.WindowSeconds),
            QueueLimit = 0,
        });
    }

    private static async ValueTask WriteRateLimitProblemAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "The rate limit for this caller was reached. Try again later.",
            },
        });
    }
}
