using System.Data;
using Microsoft.Data.SqlClient;
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
