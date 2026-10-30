using System.Collections.Concurrent;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>
/// A lock that every instance of the app sees, for work that must not run in two places at once:
/// a recurring job, a one-off backfill, warming a cache.
/// </summary>
internal interface IDistributedLock
{
    /// <summary>
    /// Takes the lock named <paramref name="name"/> without waiting. Returns null when someone else
    /// holds it; otherwise dispose the result to release it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Without a database there is one instance of the app, so a lock in process memory is enough. Add a
/// database before running more than one instance.
/// </summary>
internal sealed class InProcessDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);

    public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var held = new Held(this, name);
        return Task.FromResult<IAsyncDisposable?>(_held.TryAdd(name, held) ? held : null);
    }

    private sealed class Held(InProcessDistributedLock owner, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner._held.TryRemove(KeyValuePair.Create(name, this));
            return ValueTask.CompletedTask;
        }
    }
}
