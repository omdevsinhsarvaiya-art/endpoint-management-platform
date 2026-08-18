using EndpointPlatform.Domain.Groups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceGroupConfiguration : IEntityTypeConfiguration<DeviceGroup>
{
    public void Configure(EntityTypeBuilder<DeviceGroup> builder)
    {
        builder.ToTable("device_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.OrganizationId).IsRequired();
        builder.Property(g => g.Name).HasMaxLength(200).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(512).IsRequired();
        builder.Property(g => g.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>().WithMany()
            .HasForeignKey(g => g.OrganizationId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.OrganizationId, g.Name })
            .IsUnique().HasDatabaseName("ix_device_groups_organization_id_name");
    }
}

internal sealed class DeviceGroupMembershipConfiguration : IEntityTypeConfiguration<DeviceGroupMembership>
{
    public void Configure(EntityTypeBuilder<DeviceGroupMembership> builder)
    {
        builder.ToTable("device_group_memberships");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.GroupId).IsRequired();
        builder.Property(m => m.DeviceId).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt).IsRequired();

        builder.HasOne<DeviceGroup>().WithMany()
            .HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Domain.Devices.Device>().WithMany()
            .HasForeignKey(m => m.DeviceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.GroupId, m.DeviceId })
            .IsUnique().HasDatabaseName("ix_device_group_memberships_group_device");
        builder.HasIndex(m => m.DeviceId).HasDatabaseName("ix_device_group_memberships_device_id");
    }
}
