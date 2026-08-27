using EndpointPlatform.Domain.Drivers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DriverPackageConfiguration : IEntityTypeConfiguration<DriverPackage>
{
    public void Configure(EntityTypeBuilder<DriverPackage> builder)
    {
        builder.ToTable("driver_packages");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Version).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Provider).HasMaxLength(256);
        builder.Property(p => p.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(p => p.FileName).HasMaxLength(256).IsRequired();
        builder.Property(p => p.SizeBytes).IsRequired();
        builder.Property(p => p.InfFileName).HasMaxLength(256).IsRequired();
        builder.Property(p => p.HardwareId).HasMaxLength(512).IsRequired();
        builder.Property(p => p.DriverVersion).HasMaxLength(64);

        // Required in the database as well as the constructor. The signer pin is the
        // control that stops a trusted-but-wrong publisher installing kernel code, so
        // a null must be impossible even for a row written by something other than
        // the domain.
        builder.Property(p => p.RequiredSignerSubject).HasMaxLength(512).IsRequired();

        builder.Property(p => p.CreatedByUserId).IsRequired();
        builder.Property(p => p.CreatedByDisplay).HasMaxLength(256).IsRequired();
        builder.Property(p => p.IsWithdrawn).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // One row per content hash per organization: re-uploading identical bytes is
        // a duplicate, not a second package, and the content store is addressed by
        // hash so two rows would share one file.
        builder.HasIndex(p => new { p.OrganizationId, p.Sha256 })
            .IsUnique()
            .HasDatabaseName("ux_driver_packages_organization_sha256");

        // "Which packages can I install on this device" -- the hardware id is how a
        // console narrows the catalogue to what a machine can actually use.
        builder.HasIndex(p => p.HardwareId)
            .HasDatabaseName("ix_driver_packages_hardware_id");
    }
}
