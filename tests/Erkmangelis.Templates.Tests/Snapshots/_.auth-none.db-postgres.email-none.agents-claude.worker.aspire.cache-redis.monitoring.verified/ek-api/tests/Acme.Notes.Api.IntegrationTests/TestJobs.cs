using System.Collections.Concurrent;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>A job for failure tests: it throws while its key is in <see cref="Broken"/>.</summary>
public sealed class FlakyJob : IJob<string>
{
    public static ConcurrentDictionary<string, int> Runs { get; } = new();

    public static ConcurrentDictionary<string, bool> Broken { get; } = new();

    public Task RunAsync(string key, CancellationToken cancellationToken)
    {
        Runs.AddOrUpdate(key, 1, (_, runs) => runs + 1);
        return Broken.ContainsKey(key) ? throw new InvalidOperationException("Broken on purpose.") : Task.CompletedTask;
    }
}
