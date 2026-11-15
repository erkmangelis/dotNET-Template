using System.Collections.Concurrent;

namespace Acme.Notes.Api.Infrastructure.Idempotency;

/// <summary>Keeps idempotency keys in process memory, which is enough while notes live there too.</summary>
internal sealed class InMemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(string Scope, string Key), IdempotencyRecord> _records = new();

    public Task<IdempotencyRecord?> TryReserveAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        var id = (record.Scope, record.Key);
        if (_records.TryGetValue(id, out var existing) && existing.ExpiresAt <= time.GetUtcNow())
        {
            _records.TryRemove(KeyValuePair.Create(id, existing));
        }

        return Task.FromResult(_records.TryAdd(id, record) ? null : _records.GetValueOrDefault(id));
    }

    public Task CompleteAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        _records[(record.Scope, record.Key)] = record;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        _records.TryRemove((record.Scope, record.Key), out _);
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var (id, record) in _records)
        {
            if (record.ExpiresAt <= now && _records.TryRemove(KeyValuePair.Create(id, record)))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }
}
