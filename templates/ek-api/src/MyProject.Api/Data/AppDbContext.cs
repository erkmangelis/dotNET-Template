#if (UseIdentity)
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
#endif
using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Data;

#if (UseIdentity)
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
#else
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
#endif
{
    public DbSet<Note> Notes => Set<Note>();

#if (UseIdentity)
    /// <summary>Keys that sign bearer tokens, kept in the database so tokens survive restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

#endif
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
