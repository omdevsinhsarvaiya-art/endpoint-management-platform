using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceBitLockerStatusConfiguration : IEntityTypeConfiguration<DeviceBitLockerStatus>
{
    public void Configure(EntityTypeBuilder<DeviceBitLockerStatus> builder)
    {
        builder.ToTable("device_bitlocker_status");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.DeviceId).IsRequired();

        // Stored as text so reordering the enum can never reinterpret history, the
        // same stance the task types take.
        builder.Property(s => s.Availability)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(s => s.CollectedAt).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.DeviceId)
            .IsUnique()
            .HasDatabaseName("ux_device_bitlocker_status_device_id");
    }
}

internal sealed class DeviceBitLockerVolumeConfiguration : IEntityTypeConfiguration<DeviceBitLockerVolume>
{
    public void Configure(EntityTypeBuilder<DeviceBitLockerVolume> builder)
    {
        builder.ToTable("device_bitlocker_volumes");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.DeviceId).IsRequired();
        builder.Property(v => v.DeviceIdentifier).HasMaxLength(256).IsRequired();
        builder.Property(v => v.DriveLetter).HasMaxLength(8);
        builder.Property(v => v.PersistentVolumeId).HasMaxLength(128);

        // Protector GUIDs: identifiers, never key material. Bounded so a hostile
        // payload cannot use the column as storage.
        builder.Property(v => v.RecoveryProtectorIds).HasMaxLength(1024);

        // Startup protectors, in columns of their own. Separate storage is the point:
        // automatic recovery-key escrow derives its targets from
        // RecoveryProtectorIds, so a TPM or TPM+PIN id has no column from which it
        // could ever reach that query.
        builder.Property(v => v.TpmProtectorIds).HasMaxLength(1024);
        builder.Property(v => v.TpmPinProtectorIds).HasMaxLength(1024);

        builder.Property(v => v.CollectedAt).IsRequired();
        builder.Property(v => v.CreatedAt).IsRequired();
        builder.Property(v => v.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(v => v.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(v => v.DeviceId)
            .HasDatabaseName("ix_device_bitlocker_volumes_device_id");

        // Backs the fleet question this feature exists for: which volumes are not
        // protected. Both raw statuses, because "encrypted" and "protected" are
        // different facts and the unprotected set needs both.
        builder.HasIndex(v => new { v.ConversionStatus, v.ProtectionStatus })
            .HasDatabaseName("ix_device_bitlocker_volumes_status");
    }
}
