#if (UseSqlite)
using Microsoft.Data.Sqlite;
#elif (UseSqlServer)
using Microsoft.Data.SqlClient;
#endif
using Microsoft.EntityFrameworkCore;
#if (UsePostgres)
using Npgsql;
#endif

namespace MyProject.Api.Data;

internal static class DatabaseErrors
{
#if (UsePostgres)
    /// <summary>Whether the save failed because a unique index or primary key already holds the value.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
#elif (UseSqlServer)
    // 2601: duplicate key in a unique index. 2627: unique or primary key constraint.
    /// <summary>Whether the save failed because a unique index or primary key already holds the value.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
#else
    // SQLITE_CONSTRAINT_UNIQUE (2067) and SQLITE_CONSTRAINT_PRIMARYKEY (1555).
    /// <summary>Whether the save failed because a unique index or primary key already holds the value.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };
#endif
}
