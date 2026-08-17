using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PlatformUserRoleConfiguration : IEntityTypeConfiguration<PlatformUserRole>
{
    public void Configure(EntityTypeBuilder<PlatformUserRole> builder)
    {
        builder.ToTable("platform_user_roles");

        builder.HasKey(ur => new { ur.PlatformUserId, ur.RoleId });

        builder.HasOne(ur => ur.Role)
            .WithMany()
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ur => ur.RoleId)
            .HasDatabaseName("ix_platform_user_roles_role_id");
    }
}
