using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyProject.Api.Data;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
#if (UseSqlServer)
        // Too wide for a clustered key (900 bytes), so the table stays a heap with a nonclustered key.
        builder.HasKey(record => new { record.Scope, record.Key }).IsClustered(false);
#else
        builder.HasKey(record => new { record.Scope, record.Key });
#endif
        builder.Property(record => record.Scope).HasMaxLength(IdempotencyRecord.ScopeMaxLength);
        builder.Property(record => record.Key).HasMaxLength(IdempotencyRecord.KeyMaxLength);
        builder.Property(record => record.RequestHash).HasMaxLength(64);
        builder.HasIndex(record => record.ExpiresAt);
    }
}
