using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Notes.Api.Data;

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.ToTable("Files");
        builder.HasKey(file => file.Id);
        builder.Property(file => file.Id).ValueGeneratedNever();
        builder.Property(file => file.FileName).HasMaxLength(StoredFile.FileNameMaxLength);
        builder.Property(file => file.ContentType).HasMaxLength(100);
        builder.Property(file => file.Sha256).HasMaxLength(64);
        builder.Property(file => file.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(file => file.RejectionReason).HasMaxLength(200);
        builder.Ignore(file => file.ObjectKey);
        builder.Ignore(file => file.ThumbnailKey);
        builder.HasIndex(file => file.CreatedAt);
        // Cleanup looks for files whose time is up.
        builder.HasIndex(file => file.ExpiresAt);
    }
}
