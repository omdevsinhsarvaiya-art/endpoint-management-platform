using EndpointPlatform.Domain.Software;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class SoftwarePackageConfiguration : IEntityTypeConfiguration<SoftwarePackage>
{
    public void Configure(EntityTypeBuilder<SoftwarePackage> builder)
    {
        builder.ToTable("software_packages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Version).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Publisher).HasMaxLength(256);
        builder.Property(p => p.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(p => p.FileName).HasMaxLength(256).IsRequired();
        builder.Property(p => p.SizeBytes).IsRequired();
        builder.Property(p => p.MsiProductCode).HasMaxLength(38).IsRequired();
        builder.Property(p => p.RequiredSignerSubject).HasMaxLength(512);
        builder.Property(p => p.CreatedByUserId).IsRequired();
        builder.Property(p => p.CreatedByDisplay).HasMaxLength(256).IsRequired();
        builder.Property(p => p.IsWithdrawn).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // One package row per (org, content hash): the same bytes are registered once.
        builder.HasIndex(p => new { p.OrganizationId, p.Sha256 })
            .IsUnique()
            .HasDatabaseName("ix_software_packages_organization_id_sha256");
    }
}
