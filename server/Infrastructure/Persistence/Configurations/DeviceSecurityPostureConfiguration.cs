using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceSecurityPostureConfiguration : IEntityTypeConfiguration<DeviceSecurityPosture>
{
    public void Configure(EntityTypeBuilder<DeviceSecurityPosture> builder)
    {
        builder.ToTable("device_security_posture");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.DeviceId).IsRequired();
        builder.Property(p => p.TpmSpecVersion).HasMaxLength(32);
        builder.Property(p => p.BitLockerSystemDriveStatus).HasMaxLength(32);
        builder.Property(p => p.CollectedAt).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithOne()
            .HasForeignKey<DeviceSecurityPosture>(p => p.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.DeviceId)
            .IsUnique()
            .HasDatabaseName("ix_device_security_posture_device_id");
    }
}
