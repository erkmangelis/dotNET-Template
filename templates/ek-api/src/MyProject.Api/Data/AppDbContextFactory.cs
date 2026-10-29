using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyProject.Api.Data;

/// <summary>
/// Lets <c>dotnet ef</c> and the migration bundle create the context without starting the app.
/// The bundle reads <c>ConnectionStrings__Default</c> from the environment, the same variable the
/// API uses; adding a migration needs no database, so a placeholder is used when it is not set.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=design_time";

        return new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
    }
}
