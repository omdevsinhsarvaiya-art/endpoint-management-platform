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

        // Application discovery (agent 1.9.0). Null for rows an older agent
        // reported; the limits are the wire contract's.
        builder.Property(s => s.IdentityKind).HasMaxLength(32);
        builder.Property(s => s.StableKey).HasMaxLength(1024);
        builder.Property(s => s.VersionKey).HasMaxLength(256);
        builder.Property(s => s.Confidence).HasMaxLength(16);
        builder.Property(s => s.Category).HasMaxLength(32);
        builder.Property(s => s.PackageFamilyName).HasMaxLength(256);
        builder.Property(s => s.PackageFullName).HasMaxLength(256);
        builder.Property(s => s.UpgradeCode).HasMaxLength(64);
        builder.Property(s => s.ExecutablePath).HasMaxLength(512);
        builder.Property(s => s.SignerSubject).HasMaxLength(512);
        builder.Property(s => s.SignatureStatus).HasMaxLength(16);

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

        // "Which devices have this application", by the identity that survives
        // an update -- the query a block rule will one day run.
        builder.HasIndex(s => s.StableKey)
            .HasDatabaseName("ix_device_software_stable_key");

        // The console's default view hides frameworks, inbox apps and components.
        builder.HasIndex(s => s.Category)
            .HasDatabaseName("ix_device_software_category");
    }
}

internal sealed class DeviceSoftwareEvidenceConfiguration : IEntityTypeConfiguration<DeviceSoftwareEvidence>
{
    public void Configure(EntityTypeBuilder<DeviceSoftwareEvidence> builder)
    {
        builder.ToTable("device_software_evidence");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.DeviceSoftwareId).IsRequired();
        builder.Property(e => e.Ordinal).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(384);
        builder.Property(e => e.Detail).HasMaxLength(512);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // Replaced with its software row on every inventory: the database
        // removes the evidence when the row goes.
        builder.HasOne<DeviceSoftware>()
            .WithMany()
            .HasForeignKey(e => e.DeviceSoftwareId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.DeviceSoftwareId)
            .HasDatabaseName("ix_device_software_evidence_device_software_id");
    }
}
