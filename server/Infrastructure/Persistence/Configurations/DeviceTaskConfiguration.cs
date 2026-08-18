using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceTaskConfiguration : IEntityTypeConfiguration<DeviceTask>
{
    public void Configure(EntityTypeBuilder<DeviceTask> builder)
    {
        builder.ToTable("device_tasks");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.DeviceId).IsRequired();

        builder.Property(t => t.Type)
            .HasConversion<string>()
            .HasMaxLength(48)
            .IsRequired();

        builder.Property(t => t.PayloadJson).HasColumnType("jsonb");

        builder.Property(t => t.Status)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();

        builder.Property(t => t.CreatedByUserId).IsRequired();
        builder.Property(t => t.CreatedByDisplay).HasMaxLength(320).IsRequired();
        builder.Property(t => t.ExpiresAt).IsRequired();
        builder.Property(t => t.ResultJson).HasColumnType("jsonb");
        builder.Property(t => t.ResultMessage).HasMaxLength(1024);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        // Concurrency guard: two agent polls (or a poll racing a cancel) must not
        // both transition the same task. xmin turns the loser into a retry.
        builder.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();

        builder.HasOne<Domain.Devices.Device>()
            .WithMany()
            .HasForeignKey(t => t.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The agent poll: "queued tasks for this device, oldest first".
        builder.HasIndex(t => new { t.DeviceId, t.Status })
            .HasDatabaseName("ix_device_tasks_device_id_status");

        // Admin task list per organization, newest first.
        builder.HasIndex(t => new { t.OrganizationId, t.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_device_tasks_organization_id_created_at");

        // Expiry sweep scans non-terminal tasks past their deadline.
        builder.HasIndex(t => t.ExpiresAt)
            .HasDatabaseName("ix_device_tasks_expires_at");
    }
}
