using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceServiceEntryConfiguration : IEntityTypeConfiguration<DeviceServiceEntry>
{
    public void Configure(EntityTypeBuilder<DeviceServiceEntry> builder)
    {
        builder.ToTable("device_services");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.DeviceId).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(256).IsRequired();
        builder.Property(s => s.DisplayName).HasMaxLength(384).IsRequired();
        builder.Property(s => s.Status).HasMaxLength(32).IsRequired();
        builder.Property(s => s.StartMode).HasMaxLength(32).IsRequired();
        builder.Property(s => s.CollectedAt).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Device>().WithMany().HasForeignKey(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.DeviceId).HasDatabaseName("ix_device_services_device_id");
    }
}

internal sealed class DeviceProcessEntryConfiguration : IEntityTypeConfiguration<DeviceProcessEntry>
{
    public void Configure(EntityTypeBuilder<DeviceProcessEntry> builder)
    {
        builder.ToTable("device_processes");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.DeviceId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(256).IsRequired();
        builder.Property(p => p.ExecutablePath).HasMaxLength(512);
        builder.Property(p => p.CollectedAt).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasOne<Device>().WithMany().HasForeignKey(p => p.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => p.DeviceId).HasDatabaseName("ix_device_processes_device_id");
    }
}
