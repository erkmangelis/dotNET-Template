using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

internal static class DatabaseErrors
{
    // 2601: duplicate key in a unique index. 2627: unique or primary key constraint.
    /// <summary>Whether the save failed because a unique index or primary key already holds the value.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
