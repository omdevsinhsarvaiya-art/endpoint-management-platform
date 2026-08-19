using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AdminDeviceScopeConfiguration : IEntityTypeConfiguration<AdminDeviceScope>
{
    public void Configure(EntityTypeBuilder<AdminDeviceScope> builder)
    {
        builder.ToTable("admin_device_scopes");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.PlatformUserId).IsRequired();
        builder.Property(s => s.DeviceGroupId).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // Scope rows die with the administrator or the group they reference: a dangling
        // scope row must never be able to widen anyone's authority.
        builder.HasOne<PlatformUser>().WithMany()
            .HasForeignKey(s => s.PlatformUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DeviceGroup>().WithMany()
            .HasForeignKey(s => s.DeviceGroupId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.PlatformUserId, s.DeviceGroupId })
            .IsUnique()
            .HasDatabaseName("ix_admin_device_scopes_user_id_group_id");
    }
}
