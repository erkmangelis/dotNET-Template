# Hand-written SQL with Dapper

EF Core is the default for every query and every write. Reach for [Dapper](https://github.com/DapperLib/Dapper)
(Apache-2.0) only for a read path you have measured as hot, where EF's generated SQL or its
materialization costs too much: a large export, a dashboard aggregate, a report with window functions.

## Rules

- **Reads only.** Writes go through `AppDbContext`, so `AuditableEntityInterceptor` stamps record
  metadata, turns deletes into soft deletes and writes the audit history. SQL that writes skips all three.
- **Filter soft-deleted rows yourself.** EF adds `"DeletedAt" IS NULL` from the `SoftDelete` query
  filter; your SQL must say it.
- **Parameters, never string building.** Values go in `@Name` parameters. Only identifiers you chose
  (a column from an allow-list, as `SortFields` does) may be put into the SQL text.
- **Use the context's connection.** `db.Database.GetDbConnection()` reuses the connection string,
  the pool and any open transaction. Do not open your own connection.
- **Keep a test** that compares the result with the same query through EF, so a model change that
  breaks the SQL fails the build instead of production.

## Setup

Add the package to `Directory.Packages.props` and the API project:

```xml
<PackageVersion Include="Dapper" Version="2.1.89" />
```

```xml
<PackageReference Include="Dapper" />
```

## Example: note titles for a fast picker

Put the query next to the feature it serves, for example `Features/Notes/NoteSummaries.cs`:

```csharp
using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Features.Notes;

internal sealed record NoteSummary(Guid Id, string Title, DateTimeOffset CreatedAt);

internal sealed class NoteSummaries(AppDbContext db)
{
    private const string Sql = """
        SELECT "Id", "Title", "CreatedAt"
        FROM "Notes"
        WHERE "DeletedAt" IS NULL
        ORDER BY "CreatedAt" DESC, "Id" DESC
        LIMIT @Take
        """;

    // Npgsql reads timestamptz as a UTC DateTime, so read that and convert.
    private sealed record Row(Guid Id, string Title, DateTime CreatedAt);

    public async Task<IReadOnlyList<NoteSummary>> ListAsync(int take, CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();
        var command = new CommandDefinition(Sql, new { Take = take }, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<Row>(command);
        return [.. rows.Select(row => new NoteSummary(row.Id, row.Title, new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)))];
    }
}
```

Register it with `builder.Services.AddScoped<NoteSummaries>();` and call it from an endpoint like
any other service. Dapper opens and closes the connection when it finds it closed.

Table and column names are the EF names: the `DbSet` property for tables and the property name for
columns, unless a configuration in `Data/` sets another. A migration that renames one breaks this SQL,
which is what the comparison test is for.
