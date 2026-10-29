using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MyProject.Api.Data;

internal static class DatabaseErrors
{
    /// <summary>Whether the save failed because a unique index or primary key already holds the value.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
