using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

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
