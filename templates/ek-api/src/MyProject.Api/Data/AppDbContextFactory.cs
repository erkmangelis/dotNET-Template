using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyProject.Api.Data;

/// <summary>
/// Lets <c>dotnet ef</c> create the context without starting the app, so migrations and
/// migration bundles never need real settings or secrets. The connection string is a placeholder.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=design_time").Options);
}
