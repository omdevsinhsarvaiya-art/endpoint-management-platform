using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceDriverConfiguration : IEntityTypeConfiguration<DeviceDriver>
{
    public void Configure(EntityTypeBuilder<DeviceDriver> builder)
    {
        builder.ToTable("device_drivers");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.DeviceId).IsRequired();
        builder.Property(d => d.InstanceId).HasMaxLength(512).IsRequired();
        builder.Property(d => d.DeviceName).HasMaxLength(384).IsRequired();
        builder.Property(d => d.DeviceClass).HasMaxLength(128);
        builder.Property(d => d.Manufacturer).HasMaxLength(256);
        builder.Property(d => d.DriverProvider).HasMaxLength(256);
        builder.Property(d => d.DriverVersion).HasMaxLength(64);
        builder.Property(d => d.InfName).HasMaxLength(256);
        builder.Property(d => d.CollectedAt).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => d.DeviceId)
            .HasDatabaseName("ix_device_drivers_device_id");

        // Backs the fleet-wide question this feature exists to answer: which
        // machines have a device in a problem state. Filtered so the index covers
        // only the rows anyone searches by -- on a healthy estate nearly every row
        // has problem_code 0, and indexing those would cost far more than it pays.
        builder.HasIndex(d => d.ProblemCode)
            .HasFilter("problem_code IS NOT NULL AND problem_code <> 0")
            .HasDatabaseName("ix_device_drivers_problem_code");

        // "Who else is running this driver version" -- the question asked the
        // moment one endpoint's fault is traced to a bad driver release.
        builder.HasIndex(d => new { d.DriverProvider, d.DriverVersion })
            .HasDatabaseName("ix_device_drivers_provider_version");
    }
}
