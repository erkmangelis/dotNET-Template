#if (UseIdentity)
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
#endif
using Microsoft.EntityFrameworkCore;
#if (UseSqlite)
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
#endif

namespace MyProject.Api.Data;

#if (UseIdentity)
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
#else
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
#endif
{
    public DbSet<Note> Notes => Set<Note>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
#if (UseStorage)

    public DbSet<StoredFile> Files => Set<StoredFile>();
#endif
#if (UseAuth)

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
#endif

#if (UseIdentity)
    /// <summary>Keys that sign bearer tokens, kept in the database so tokens survive restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

#endif
#if (UseSqlite)
    /// <summary>
    /// SQLite cannot compare or sort <see cref="DateTimeOffset"/> values, so they are stored as UTC ticks.
    /// Every time the app writes is UTC already.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

#endif
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
#if (UseSqlite)

/// <summary>Stores a <see cref="DateTimeOffset"/> as UTC ticks, which SQLite can compare and sort.</summary>
internal sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
#endif
