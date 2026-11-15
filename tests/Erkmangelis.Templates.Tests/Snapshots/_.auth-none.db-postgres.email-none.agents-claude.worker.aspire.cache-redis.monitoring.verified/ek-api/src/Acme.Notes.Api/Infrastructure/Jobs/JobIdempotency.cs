using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>
/// Makes a job's effect happen once per key, however often the job runs: a retry after a failure, a
/// second delivery after a crash, a requeue. Keys are kept with the request idempotency keys (scope
/// <c>job:{name}</c>) for <see cref="JobsOptions.IdempotencyRetentionDays"/>.
/// </summary>
/// <remarks>
/// The key and every change the work saves through the app's database context commit in one
/// transaction: either both are saved or neither is. A second run that starts while the first still
/// holds the key waits for it, then skips. Work outside the database (an email, a call to another
/// service) is not covered by the transaction; give that service the key too, if it takes one.
/// </remarks>
internal sealed class JobIdempotency(IIdempotencyStore store, AppDbContext db, TimeProvider time, IOptions<JobsOptions> options)
{
    private const string Done = "done";

    /// <summary>Runs <paramref name="work"/> unless a run with the same <paramref name="jobName"/> and <paramref name="key"/> already finished. Returns whether it ran.</summary>
    public async Task<bool> RunOnceAsync(string jobName, string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var record = new IdempotencyRecord
        {
            Scope = $"job:{jobName}",
            Key = key,
            RequestHash = Done,
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.IdempotencyRetentionDays),
        };
        if (await store.TryReserveAsync(record, cancellationToken) is { } existing)
        {
            return existing.StatusCode is not null
                ? false
                : throw new InvalidOperationException($"Job {jobName} with key {key} is running elsewhere; try again later.");
        }

        await work(cancellationToken);
        record.StatusCode = StatusCodes.Status200OK;
        await store.CompleteAsync(record, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
