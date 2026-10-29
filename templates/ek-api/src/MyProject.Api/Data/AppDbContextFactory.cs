#if (UseIdentity)
using Microsoft.AspNetCore.Identity;
#endif
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
#if (UsePostgres)
            ?? "Host=localhost;Database=design_time";
#elif (UseSqlServer)
            ?? "Server=localhost;Database=design_time;TrustServerCertificate=True";
#else
            ?? "Data Source=design_time.db";
#endif

#if (UseIdentity)
        // Identity picks its tables from IdentityOptions, so give the design-time context the app's schema version.
        var identityOptions = new ServiceCollection()
            .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = AuthSetup.SchemaVersion)
            .BuildServiceProvider();

        return new(new DbContextOptionsBuilder<AppDbContext>()
            .UseAppDatabase(connectionString)
            .UseApplicationServiceProvider(identityOptions)
            .Options);
#else
        return new(new DbContextOptionsBuilder<AppDbContext>().UseAppDatabase(connectionString).Options);
#endif
    }
}
