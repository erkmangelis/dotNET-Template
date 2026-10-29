#if (UsePostgres || UseSqlite)
using System.Security.Cryptography;
using System.Text;
#elif (!UseDatabase)
using System.Collections.Concurrent;
#endif
#if (UseSqlServer)
using System.Data;
#endif
#if (UseSqlServer)
using Microsoft.Data.SqlClient;
#elif (UseSqlite)
using Microsoft.Data.Sqlite;
#endif
#if (UseDatabase)
using Microsoft.Extensions.Options;
#endif
#if (UsePostgres)
using Npgsql;
#endif

namespace MyProject.Api.Infrastructure.Jobs;

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
#if (UsePostgres)

/// <summary>
/// A PostgreSQL session advisory lock, held on a connection of its own. If the app dies, the database
/// ends the session and the lock goes with it.
/// </summary>
internal sealed class PostgresDistributedLock(IOptions<DatabaseOptions> options) : IDistributedLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        // Advisory locks take a 64-bit key: the first 8 bytes of the name's SHA-256.
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var connection = new NpgsqlConnection(options.Value.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return new Held(connection, key);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class Held(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
#elif (UseSqlServer)

/// <summary>
/// A SQL Server application lock (<c>sp_getapplock</c>) owned by a session of its own. If the app dies,
/// the database ends the session and the lock goes with it.
/// </summary>
internal sealed class SqlServerDistributedLock(IOptions<DatabaseOptions> options) : IDistributedLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("sp_getapplock", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@Resource", name);
            command.Parameters.AddWithValue("@LockMode", "Exclusive");
            command.Parameters.AddWithValue("@LockOwner", "Session");
            command.Parameters.AddWithValue("@LockTimeout", 0);
            var result = command.Parameters.Add("@Result", SqlDbType.Int);
            result.Direction = ParameterDirection.ReturnValue;
            await command.ExecuteNonQueryAsync(cancellationToken);

            // 0 or 1: granted. Negative: held elsewhere, or an error.
            if ((int)result.Value >= 0)
            {
                return new Held(connection, name);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class Held(SqlConnection connection, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new SqlCommand("sp_releaseapplock", connection) { CommandType = CommandType.StoredProcedure };
                command.Parameters.AddWithValue("@Resource", name);
                command.Parameters.AddWithValue("@LockOwner", "Session");
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
#elif (UseSqlite)

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
#else

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
#endif
