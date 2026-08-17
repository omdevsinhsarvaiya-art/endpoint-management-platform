using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Key)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(r => r.DisplayName)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.Description)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(r => r.IsBuiltIn).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(r => r.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Built-in roles are global (organization_id IS NULL) and their keys must be
        // globally unique. Two partial unique indexes express that precisely:
        // one key per organization for custom roles, one global key for built-ins.
        builder.HasIndex(r => new { r.OrganizationId, r.Key })
            .IsUnique()
            .HasFilter("organization_id IS NOT NULL")
            .HasDatabaseName("ix_roles_organization_id_key");

        builder.HasIndex(r => r.Key)
            .IsUnique()
            .HasFilter("organization_id IS NULL")
            .HasDatabaseName("ix_roles_key_builtin");

        builder.HasMany(r => r.Permissions)
            .WithOne(p => p.Role)
            .HasForeignKey(p => p.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Permissions)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_permissions");
    }
}
