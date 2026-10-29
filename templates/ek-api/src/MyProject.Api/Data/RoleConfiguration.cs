using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyProject.Api.Data;

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
                Id = "7c1d2f4e-0a6b-4c3d-9e8f-1a2b3c4d5e01",
                Name = Roles.Admin,
                NormalizedName = Roles.Admin.ToUpperInvariant(),
                ConcurrencyStamp = "7c1d2f4e-0a6b-4c3d-9e8f-1a2b3c4d5e11",
            },
            new IdentityRole
            {
                Id = "7c1d2f4e-0a6b-4c3d-9e8f-1a2b3c4d5e02",
                Name = Roles.Auditor,
                NormalizedName = Roles.Auditor.ToUpperInvariant(),
                ConcurrencyStamp = "7c1d2f4e-0a6b-4c3d-9e8f-1a2b3c4d5e12",
            });
}
