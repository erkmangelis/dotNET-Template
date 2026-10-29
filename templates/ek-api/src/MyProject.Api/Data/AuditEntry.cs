namespace MyProject.Api.Data;

internal enum AuditAction
{
    Created,
    Updated,
    Deleted,
}

/// <summary>A property's value before and after a change.</summary>
internal sealed record AuditChange(object? From, object? To);

/// <summary>
/// One change to an <see cref="IAudited"/> entity: what changed, when, by whom, and the values before
/// and after. Written by <see cref="AuditableEntityInterceptor"/> in the same transaction as the change.
/// </summary>
internal sealed class AuditEntry
{
    public const int EntityTypeMaxLength = 100;
    public const int EntityIdMaxLength = 100;

    public Guid Id { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public AuditAction Action { get; init; }

    /// <summary>A JSON object of property name to <see cref="AuditChange"/>.</summary>
    public required string Changes { get; init; }
#if (UseAuth)

    /// <summary>The signed-in user who made the change, or null for changes outside a request.</summary>
    public string? ActorId { get; init; }
#endif

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>The request's trace id, to find its logs and spans.</summary>
    public string? TraceId { get; init; }
}
