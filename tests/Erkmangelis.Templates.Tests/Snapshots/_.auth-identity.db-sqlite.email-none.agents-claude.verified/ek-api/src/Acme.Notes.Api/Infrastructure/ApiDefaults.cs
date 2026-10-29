using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Asp.Versioning;
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
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ICurrentActor, HttpCurrentActor>();

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
            context.ProblemDetails.Extensions.TryAdd(
                "traceId",
                Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
        });
        services.AddValidation();
        // Versions live in the URL (/v1, /v2). Responses list the versions an endpoint supports in
        // api-supported-versions and the deprecated ones in api-deprecated-versions.
        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1);
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
                options.ReportApiVersions = true;
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            })
            // One OpenAPI document per version: /openapi/v1.json, /openapi/v2.json.
            .AddOpenApi(options => options.Document.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Acme.Notes API";
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

        services.AddIdempotency();

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
        app.UseAuthentication();
        // Before authorization: a policy with several schemes replaces the result this check reads.
        app.UseBrowserSessionAntiforgery();
        app.UseAuthorization();
        app.UseRateLimiter();
        // After authentication and rate limiting, so keys belong to a caller and rejected requests store nothing.
        app.UseMiddleware<IdempotencyMiddleware>();

        if (api.ExposeOpenApi)
        {
            app.MapOpenApi().WithDocumentPerVersion().DisableRateLimiting();
            app.MapScalarApiReference(options => options.AddDocuments(ApiVersions.Documents)).DisableRateLimiting();
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
