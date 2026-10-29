using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
