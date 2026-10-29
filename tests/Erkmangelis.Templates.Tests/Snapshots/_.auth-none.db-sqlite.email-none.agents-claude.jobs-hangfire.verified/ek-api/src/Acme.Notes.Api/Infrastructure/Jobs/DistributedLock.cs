using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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
/// An exclusive lock on a small file next to the database, so it holds across processes on this
/// machine (an API and a worker sharing the volume). If a process dies, the system drops its locks.
/// </summary>
internal sealed class FileDistributedLock(IOptions<DatabaseOptions> options) : IDistributedLock
{
    public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var database = new SqliteConnectionStringBuilder(options.Value.ConnectionString).DataSource;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16];
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database)) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(database)}-lock-{hash}");
        try
        {
            // The file stays: deleting it on release would let two processes lock two different files.
            return Task.FromResult<IAsyncDisposable?>(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }
}
