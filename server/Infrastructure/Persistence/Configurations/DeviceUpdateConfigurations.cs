using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceUpdateStatusConfiguration : IEntityTypeConfiguration<DeviceUpdateStatus>
{
    public void Configure(EntityTypeBuilder<DeviceUpdateStatus> builder)
    {
        builder.ToTable("device_update_status");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.DeviceId).IsRequired();
        builder.Property(u => u.CollectedAt).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt).IsRequired();
        builder.HasOne<Device>().WithOne().HasForeignKey<DeviceUpdateStatus>(u => u.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(u => u.DeviceId).IsUnique().HasDatabaseName("ix_device_update_status_device_id");
    }
}

internal sealed class DeviceUpdateHistoryEntryConfiguration : IEntityTypeConfiguration<DeviceUpdateHistoryEntry>
{
    public void Configure(EntityTypeBuilder<DeviceUpdateHistoryEntry> builder)
    {
        builder.ToTable("device_update_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.DeviceId).IsRequired();
        builder.Property(h => h.Title).HasMaxLength(384).IsRequired();
        builder.Property(h => h.Operation).HasMaxLength(32).IsRequired();
        builder.Property(h => h.Result).HasMaxLength(32).IsRequired();
        builder.Property(h => h.CollectedAt).IsRequired();
        builder.Property(h => h.CreatedAt).IsRequired();
        builder.Property(h => h.UpdatedAt).IsRequired();
        builder.HasOne<Device>().WithMany().HasForeignKey(h => h.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(h => new { h.DeviceId, h.Date })
            .IsDescending(false, true).HasDatabaseName("ix_device_update_history_device_id_date");
    }
}
