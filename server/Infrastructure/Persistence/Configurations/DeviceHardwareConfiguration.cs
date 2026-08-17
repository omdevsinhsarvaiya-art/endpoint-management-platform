using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceHardwareConfiguration : IEntityTypeConfiguration<DeviceHardware>
{
    public void Configure(EntityTypeBuilder<DeviceHardware> builder)
    {
        builder.ToTable("device_hardware");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.DeviceId).IsRequired();
        builder.Property(h => h.SerialNumber).HasMaxLength(128);
        builder.Property(h => h.Manufacturer).HasMaxLength(128);
        builder.Property(h => h.Model).HasMaxLength(128);
        builder.Property(h => h.CpuName).HasMaxLength(128);
        builder.Property(h => h.DisksJson).HasColumnType("jsonb");
        builder.Property(h => h.CollectedAt).IsRequired();
        builder.Property(h => h.CreatedAt).IsRequired();
        builder.Property(h => h.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithOne()
            .HasForeignKey<DeviceHardware>(h => h.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(h => h.DeviceId)
            .IsUnique()
            .HasDatabaseName("ix_device_hardware_device_id");

        // "Find the machine with this service tag" is a real helpdesk query.
        builder.HasIndex(h => h.SerialNumber)
            .HasDatabaseName("ix_device_hardware_serial_number");
    }
}
