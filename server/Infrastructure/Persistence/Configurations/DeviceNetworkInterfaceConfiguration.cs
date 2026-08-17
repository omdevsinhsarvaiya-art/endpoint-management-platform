using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceNetworkInterfaceConfiguration : IEntityTypeConfiguration<DeviceNetworkInterface>
{
    public void Configure(EntityTypeBuilder<DeviceNetworkInterface> builder)
    {
        builder.ToTable("device_network_interfaces");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.DeviceId).IsRequired();

        builder.Property(n => n.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(n => n.MacAddress).HasMaxLength(23);
        builder.Property(n => n.IpAddressesJson).HasColumnType("jsonb");
        builder.Property(n => n.IsUp).IsRequired();
        builder.Property(n => n.CollectedAt).IsRequired();
        builder.Property(n => n.CreatedAt).IsRequired();
        builder.Property(n => n.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(n => n.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(n => n.DeviceId)
            .HasDatabaseName("ix_device_network_interfaces_device_id");

        // "Which machine has this MAC" - DHCP/switch-port investigations.
        builder.HasIndex(n => n.MacAddress)
            .HasDatabaseName("ix_device_network_interfaces_mac_address");
    }
}
