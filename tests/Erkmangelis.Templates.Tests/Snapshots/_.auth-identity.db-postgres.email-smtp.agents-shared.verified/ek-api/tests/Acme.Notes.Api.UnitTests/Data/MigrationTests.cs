using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.UnitTests.Data;

public sealed class MigrationTests
{
    [Fact]
    public void The_model_has_no_changes_missing_from_the_migrations()
    {
        using var db = new AppDbContextFactory().CreateDbContext([]);

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model changed without a migration. Run: dotnet ef migrations add <Name> --project src/Acme.Notes.Api --output-dir Data/Migrations");
    }
}
