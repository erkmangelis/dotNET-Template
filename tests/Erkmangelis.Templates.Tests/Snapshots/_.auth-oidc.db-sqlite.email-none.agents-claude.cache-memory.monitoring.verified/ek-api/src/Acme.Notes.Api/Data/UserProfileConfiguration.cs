using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Notes.Api.Data;

internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.HasKey(profile => profile.UserId);
        builder.Property(profile => profile.UserId).HasMaxLength(450);
        builder.Property(profile => profile.DisplayName).HasMaxLength(UserProfile.DisplayNameMaxLength);
        builder.Property(profile => profile.TimeZone).HasMaxLength(64);
        builder.Property(profile => profile.Locale).HasMaxLength(35);
        builder.Property(profile => profile.Theme).HasMaxLength(16);
    }
}
