using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Notes.Api.Data;

/// <summary>
/// Seeds <see cref="Roles"/> through migrations, so every environment has them without startup code.
/// The ids and stamps are fixed because seed data must not change between migrations.
/// </summary>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<IdentityRole>
{
    public void Configure(EntityTypeBuilder<IdentityRole> builder) =>
        builder.HasData(
            new IdentityRole
            {
                Id = "00000000-0000-0000-0000-000000000000",
                Name = Roles.Admin,
                NormalizedName = Roles.Admin.ToUpperInvariant(),
                ConcurrencyStamp = "00000000-0000-0000-0000-000000000000",
            },
            new IdentityRole
            {
                Id = "00000000-0000-0000-0000-000000000000",
                Name = Roles.Auditor,
                NormalizedName = Roles.Auditor.ToUpperInvariant(),
                ConcurrencyStamp = "00000000-0000-0000-0000-000000000000",
            });
}
