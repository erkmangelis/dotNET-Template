using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyProject.Api.Data;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.HasKey(record => new { record.Scope, record.Key });
        builder.Property(record => record.Scope).HasMaxLength(IdempotencyRecord.ScopeMaxLength);
        builder.Property(record => record.Key).HasMaxLength(IdempotencyRecord.KeyMaxLength);
        builder.Property(record => record.RequestHash).HasMaxLength(64);
        builder.HasIndex(record => record.ExpiresAt);
    }
}
