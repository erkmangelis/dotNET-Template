namespace MyProject.Api.Infrastructure.Idempotency;

/// <summary>
/// One <c>Idempotency-Key</c> a caller used: a hash of the request it came with and, once that
/// request finished, the response to replay when the same request is retried.
/// </summary>
internal sealed class IdempotencyRecord
{
    public const int KeyMaxLength = 255;
    public const int ScopeMaxLength = 450;

    /// <summary>Whose key it is: the signed-in user's id, or <see cref="IdempotencyMiddleware.AnonymousScope"/>.</summary>
    public required string Scope { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of the method, path, query and body, in hex.</summary>
    public required string RequestHash { get; init; }

    /// <summary>Null while the first request is still running.</summary>
    public int? StatusCode { get; set; }

    /// <summary>The response headers worth replaying, as a JSON object.</summary>
    public string? Headers { get; set; }

    public byte[]? Body { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}
