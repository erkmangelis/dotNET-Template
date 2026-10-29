using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Notes.Api.Data;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength);
        builder.Property(entry => entry.EntityId).HasMaxLength(AuditEntry.EntityIdMaxLength);
        builder.Property(entry => entry.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.TraceId).HasMaxLength(32);
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId, entry.OccurredAt });
    }
}
