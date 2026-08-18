using EndpointPlatform.Domain.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
{
    public void Configure(EntityTypeBuilder<Policy> builder)
    {
        builder.ToTable("policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.Type).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(512).IsRequired();
        builder.Property(p => p.IsEnabled).IsRequired();
        builder.Property(p => p.CurrentVersionNumber).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>().WithMany()
            .HasForeignKey(p => p.OrganizationId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Versions).WithOne()
            .HasForeignKey(v => v.PolicyId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Versions).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_versions");

        builder.HasIndex(p => p.OrganizationId).HasDatabaseName("ix_policies_organization_id");
    }
}

internal sealed class PolicyVersionConfiguration : IEntityTypeConfiguration<PolicyVersion>
{
    public void Configure(EntityTypeBuilder<PolicyVersion> builder)
    {
        builder.ToTable("policy_versions");
        builder.HasKey(v => v.Id);
        // The domain generates the Id (UUIDv7). Without this, EF sees a client-set
        // key on a child added via the Policy.Versions navigation of a TRACKED
        // policy and marks it Modified (an UPDATE that hits 0 rows) instead of
        // Added. ValueGeneratedNever makes EF treat it as a genuine insert.
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.PolicyId).IsRequired();
        builder.Property(v => v.VersionNumber).IsRequired();
        builder.Property(v => v.DesiredStateJson).HasColumnType("jsonb").IsRequired();
        builder.Property(v => v.CreatedAt).IsRequired();

        builder.HasIndex(v => new { v.PolicyId, v.VersionNumber })
            .IsUnique().HasDatabaseName("ix_policy_versions_policy_id_version_number");
    }
}

internal sealed class PolicyAssignmentConfiguration : IEntityTypeConfiguration<PolicyAssignment>
{
    public void Configure(EntityTypeBuilder<PolicyAssignment> builder)
    {
        builder.ToTable("policy_assignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.PolicyId).IsRequired();
        builder.Property(a => a.TargetType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.TargetId).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();

        builder.HasOne<Policy>().WithMany()
            .HasForeignKey(a => a.PolicyId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.PolicyId, a.TargetType, a.TargetId })
            .IsUnique().HasDatabaseName("ix_policy_assignments_policy_target");
        builder.HasIndex(a => new { a.TargetType, a.TargetId })
            .HasDatabaseName("ix_policy_assignments_target");
    }
}

internal sealed class PolicyComplianceResultConfiguration : IEntityTypeConfiguration<PolicyComplianceResult>
{
    public void Configure(EntityTypeBuilder<PolicyComplianceResult> builder)
    {
        builder.ToTable("policy_compliance_results");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.DeviceId).IsRequired();
        builder.Property(r => r.PolicyId).IsRequired();
        builder.Property(r => r.PolicyVersionId).IsRequired();
        builder.Property(r => r.PolicyVersionNumber).IsRequired();
        builder.Property(r => r.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.DeviationsJson).HasColumnType("jsonb");
        builder.Property(r => r.EvaluatedAt).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Devices.Device>().WithMany()
            .HasForeignKey(r => r.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Policy>().WithMany()
            .HasForeignKey(r => r.PolicyId).OnDelete(DeleteBehavior.Cascade);

        // One result row per (device, policy) - updated in place.
        builder.HasIndex(r => new { r.DeviceId, r.PolicyId })
            .IsUnique().HasDatabaseName("ix_policy_compliance_device_policy");
        builder.HasIndex(r => new { r.PolicyId, r.State })
            .HasDatabaseName("ix_policy_compliance_policy_state");
    }
}
