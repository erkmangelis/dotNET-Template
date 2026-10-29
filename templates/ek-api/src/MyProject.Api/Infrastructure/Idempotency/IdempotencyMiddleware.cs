using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace MyProject.Api.Infrastructure.Idempotency;

/// <summary>Marks an endpoint that honours the <c>Idempotency-Key</c> request header.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class IdempotentAttribute : Attribute;

/// <summary>
/// Makes retried writes safe. A request to an <see cref="IdempotentAttribute"/> endpoint that carries an
/// <c>Idempotency-Key</c> runs once; a retry with the same key and the same request gets the first
/// response again, with <c>Idempotent-Replayed: true</c>. The same key with a different request
/// answers 422, and a retry while the first request is still running answers 409. Server errors are
/// not kept, so the client can retry them. Keys belong to the caller who sent them.
/// </summary>
internal sealed class IdempotencyMiddleware(RequestDelegate next, IOptions<IdempotencyOptions> options, TimeProvider time)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    public const string AnonymousScope = "anonymous";

    // Headers that describe the stored body or the created resource; the rest are per response.
    private static readonly string[] _replayedHeaders = ["Content-Type", "Location", "ETag"];

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>() is null
            || !context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            await next(context);
            return;
        }

        var key = values.ToString();
        if (!IsValidKey(key))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Invalid idempotency key", $"{HeaderName} must be 1 to {IdempotencyRecord.KeyMaxLength} visible ASCII characters.");
            return;
        }

        var now = time.GetUtcNow();
        var record = new IdempotencyRecord
        {
            Scope = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? AnonymousScope,
            Key = key,
            RequestHash = await HashRequestAsync(context.Request),
            CreatedAt = now,
            ExpiresAt = now.AddHours(options.Value.RetentionHours),
        };

        var existing = await store.TryReserveAsync(record, context.RequestAborted);
        if (existing is not null)
        {
            await AnswerFromExistingAsync(context, existing, record.RequestHash);
            return;
        }

        var body = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            await store.ReleaseAsync(record, CancellationToken.None);
            throw;
        }
        finally
        {
            context.Response.Body = body;
        }

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await store.ReleaseAsync(record, CancellationToken.None);
        }
        else
        {
            record.StatusCode = context.Response.StatusCode;
            record.Headers = JsonSerializer.Serialize(_replayedHeaders
                .Where(name => context.Response.Headers.ContainsKey(name))
                .ToDictionary(name => name, name => context.Response.Headers[name].ToString()));
            record.Body = buffer.ToArray();
            // The response is already decided, so a cancelled request must not stop it being stored.
            await store.CompleteAsync(record, CancellationToken.None);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(body, context.RequestAborted);
    }

    private static async Task AnswerFromExistingAsync(HttpContext context, IdempotencyRecord existing, string requestHash)
    {
        if (existing.StatusCode is not { } statusCode)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Request in progress", $"A request with this {HeaderName} is still running. Retry later.");
            return;
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "Idempotency key reused", $"This {HeaderName} was used for a different request. Use a new key for a new request.");
            return;
        }

        context.Response.StatusCode = statusCode;
        foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(existing.Headers ?? "{}")!)
        {
            context.Response.Headers[name] = value;
        }

        context.Response.Headers[ReplayedHeaderName] = "true";
        if (existing.Body is { Length: > 0 } body)
        {
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= IdempotencyRecord.KeyMaxLength && key.All(character => character is > ' ' and <= '~');

    private static async Task<string> HashRequestAsync(HttpRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));

        request.EnableBuffering();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            hash.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ValueTask WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.StatusCode = statusCode;
        return context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = statusCode, Title = title, Detail = detail },
        });
    }
}

/// <summary>Deletes expired idempotency keys every hour.</summary>
internal sealed partial class IdempotencyCleanup(IServiceScopeFactory scopes, TimeProvider time, ILogger<IdempotencyCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().DeleteExpiredAsync(time.GetUtcNow(), stoppingToken);
                LogDeleted(logger, deleted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted {Count} expired idempotency keys")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting expired idempotency keys failed; retrying in an hour")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

internal static class IdempotencySetup
{
    public static IServiceCollection AddIdempotency(this IServiceCollection services)
    {
        services.AddOptions<IdempotencyOptions>()
            .BindConfiguration(IdempotencyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
#if (UsePostgres)
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
#else
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
#endif
        services.AddHostedService<IdempotencyCleanup>();
        return services;
    }

    /// <summary>Honours the <c>Idempotency-Key</c> header on this endpoint and documents it in OpenAPI.</summary>
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new IdempotentAttribute());
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotencyMiddleware.HeaderName,
                In = ParameterLocation.Header,
                Description = "Any unique value, for example a UUID. Retrying with the same key and request replays the first response instead of running it again.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = IdempotencyRecord.KeyMaxLength },
            });
            return Task.CompletedTask;
        });
        return builder;
    }
}
