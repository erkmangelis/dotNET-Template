using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Acme.Notes.Api.Data;

/// <summary>
/// Lets <c>dotnet ef</c> and the migration bundle create the context without starting the app.
/// The bundle reads <c>ConnectionStrings__Default</c> from the environment, the same variable the
/// API uses; adding a migration needs no database, so a placeholder is used when it is not set.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>The connection string design-time contexts use; feature contexts share it.</summary>
    public static string ConnectionString => Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? "Host=localhost;Database=design_time";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = ConnectionString;

        // Identity picks its tables from IdentityOptions, so give the design-time context the app's schema version.
        var identityOptions = new ServiceCollection()
            .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = AuthSetup.SchemaVersion)
            .BuildServiceProvider();

        return new(new DbContextOptionsBuilder<AppDbContext>()
            .UseAppDatabase(connectionString)
            .UseApplicationServiceProvider(identityOptions)
            .Options);
    }
}
