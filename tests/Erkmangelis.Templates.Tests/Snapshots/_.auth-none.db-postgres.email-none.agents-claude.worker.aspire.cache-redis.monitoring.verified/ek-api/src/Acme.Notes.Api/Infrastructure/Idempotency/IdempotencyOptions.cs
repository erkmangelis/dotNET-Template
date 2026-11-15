using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Infrastructure.Idempotency;

/// <summary>Idempotency key settings, bound from the <c>Idempotency</c> section.</summary>
internal sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a key keeps replaying its first response. Clients must not retry with a key older than this.</summary>
    [Range(1, 720)]
    public int RetentionHours { get; set; } = 24;
}
