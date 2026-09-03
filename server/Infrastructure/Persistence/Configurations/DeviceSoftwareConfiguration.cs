using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceSoftwareConfiguration : IEntityTypeConfiguration<DeviceSoftware>
{
    public void Configure(EntityTypeBuilder<DeviceSoftware> builder)
    {
        builder.ToTable("device_software");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.DeviceId).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(384).IsRequired();
        builder.Property(s => s.Version).HasMaxLength(128);
        builder.Property(s => s.Publisher).HasMaxLength(256);
        builder.Property(s => s.InstallDate).HasMaxLength(32);
        builder.Property(s => s.InstallLocation).HasMaxLength(512);
        builder.Property(s => s.Architecture).HasMaxLength(16);
        builder.Property(s => s.InstallationScope).HasMaxLength(16);
        builder.Property(s => s.InstalledForUser).HasMaxLength(256);
        builder.Property(s => s.ProductCode).HasMaxLength(64);
        builder.Property(s => s.CollectedAt).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.DeviceId)
            .HasDatabaseName("ix_device_software_device_id");

        // Fleet-wide "who has product X / publisher Y" queries.
        builder.HasIndex(s => new { s.Name, s.Version })
            .HasDatabaseName("ix_device_software_name_version");

        builder.HasIndex(s => s.Publisher)
            .HasDatabaseName("ix_device_software_publisher");

        // Relating an installed application to an approved managed package by
        // MsiProductCode. Sparse - only MSI-installed products have one.
        builder.HasIndex(s => s.ProductCode)
            .HasDatabaseName("ix_device_software_product_code");
    }
}
