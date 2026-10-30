using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

internal static class DatabaseErrors
{
    // SQLITE_CONSTRAINT_UNIQUE (2067) and SQLITE_CONSTRAINT_PRIMARYKEY (1555).
    /// <summary>Whether the save failed because a unique index or primary key already holds the value.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };
}
