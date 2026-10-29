namespace MyProject.Api.Infrastructure.Idempotency;

internal interface IIdempotencyStore
{
    /// <summary>
    /// Stores <paramref name="record"/> as in progress. Returns null when it was stored, or the record
    /// already kept under its scope and key. An expired record is replaced.
    /// </summary>
    Task<IdempotencyRecord?> TryReserveAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Saves the response on a reserved record, so retries replay it.</summary>
    Task CompleteAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Forgets a reserved record, so the request can be retried and run again.</summary>
    Task ReleaseAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
