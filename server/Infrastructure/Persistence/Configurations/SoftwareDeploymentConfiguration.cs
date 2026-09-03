using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Software;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class SoftwareDeploymentConfiguration : IEntityTypeConfiguration<SoftwareDeployment>
{
    public void Configure(EntityTypeBuilder<SoftwareDeployment> builder)
    {
        builder.ToTable("software_deployments");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.OrganizationId).IsRequired();
        builder.Property(d => d.PackageId).IsRequired();
        builder.Property(d => d.PackageName).HasMaxLength(256).IsRequired();
        builder.Property(d => d.PackageVersion).HasMaxLength(128).IsRequired();
        builder.Property(d => d.TargetType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.CreatedByUserId).IsRequired();
        builder.Property(d => d.CreatedByDisplay).HasMaxLength(256).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        // No FK to the package: a deployment is a historical record and must
        // survive whatever happens to the package afterwards. The name and
        // version it sent are copied onto the row for that reason.
        builder.HasIndex(d => new { d.OrganizationId, d.CreatedAt })
            .HasDatabaseName("ix_software_deployments_org_created");
    }
}

internal sealed class SoftwareDeploymentTargetConfiguration : IEntityTypeConfiguration<SoftwareDeploymentTarget>
{
    public void Configure(EntityTypeBuilder<SoftwareDeploymentTarget> builder)
    {
        builder.ToTable("software_deployment_targets");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.DeploymentId).IsRequired();
        builder.Property(t => t.DeviceId).IsRequired();
        builder.Property(t => t.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Reason).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.ObservedVersion).HasMaxLength(128);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        builder.HasOne<SoftwareDeployment>()
            .WithMany()
            .HasForeignKey(t => t.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(t => t.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // A device appears at most once in a deployment. The unique index is the
        // guard against double-queueing, not a convention: without it a repeated
        // or concurrent submission would install the same package twice on one
        // machine.
        builder.HasIndex(t => new { t.DeploymentId, t.DeviceId })
            .IsUnique()
            .HasDatabaseName("ux_software_deployment_targets_deployment_device");

        // Status is read by joining the task, so this is the join column.
        builder.HasIndex(t => t.TaskId)
            .HasDatabaseName("ix_software_deployment_targets_task_id");
    }
}
