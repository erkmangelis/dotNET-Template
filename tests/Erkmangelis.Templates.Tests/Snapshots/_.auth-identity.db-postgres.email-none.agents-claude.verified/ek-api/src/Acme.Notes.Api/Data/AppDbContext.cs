using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

internal sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
{
    public DbSet<Note> Notes => Set<Note>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    /// <summary>Keys that sign bearer tokens, kept in the database so tokens survive restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
