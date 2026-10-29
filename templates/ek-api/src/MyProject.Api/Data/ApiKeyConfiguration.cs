using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyProject.Api.Data;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.HasKey(key => key.Id);
        builder.Property(key => key.Id).ValueGeneratedNever();
        builder.Property(key => key.OwnerId).HasMaxLength(450);
        builder.Property(key => key.Name).HasMaxLength(ApiKey.NameMaxLength);
        builder.Property(key => key.LookupId).HasMaxLength(32);
        builder.Property(key => key.SecretHash).HasMaxLength(32);
        builder.HasIndex(key => key.LookupId).IsUnique();
        builder.HasIndex(key => key.OwnerId);
#if (UseIdentity)

        // Deleting the account deletes its keys.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(key => key.OwnerId).OnDelete(DeleteBehavior.Cascade);
#endif
    }
}
