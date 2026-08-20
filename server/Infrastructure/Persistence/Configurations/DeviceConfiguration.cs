using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.OrganizationId).IsRequired();

        builder.Property(d => d.Hostname)
            .HasMaxLength(253)
            .IsRequired();

        // Nullable on purpose: "no label" is a real state, distinct from an empty
        // one, and it is what makes the hostname fallback unambiguous.
        builder.Property(d => d.DisplayName)
            .HasMaxLength(128);

        builder.Property(d => d.MachineIdentifier)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(d => d.AgentVersion)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(d => d.OperatingSystem)
            .HasMaxLength(256);

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(d => d.EnrolledWithTokenId).IsRequired();
        builder.Property(d => d.EnrolledAt).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>()
            .WithMany()
            .HasForeignKey(d => d.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Domain.Enrollment.EnrollmentToken>()
            .WithMany()
            .HasForeignKey(d => d.EnrolledWithTokenId)
            // The token must outlive the devices it admitted - audit lineage.
            .OnDelete(DeleteBehavior.Restrict);

        // One device row per physical machine per organization. This is what turns
        // re-running the installer into re-enrollment instead of a duplicate.
        builder.HasIndex(d => new { d.OrganizationId, d.MachineIdentifier })
            .IsUnique()
            .HasDatabaseName("ix_devices_organization_id_machine_identifier");

        // Device list is sorted by recency; online/offline derives from last_seen.
        builder.HasIndex(d => new { d.OrganizationId, d.LastSeenAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_devices_organization_id_last_seen_at");

        builder.HasIndex(d => new { d.OrganizationId, d.Hostname })
            .HasDatabaseName("ix_devices_organization_id_hostname");
    }
}
