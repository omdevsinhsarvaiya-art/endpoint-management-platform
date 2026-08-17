using System.Net;
using EndpointPlatform.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log_entries");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.OccurredAt).IsRequired();

        builder.Property(a => a.ActorType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.ActorDisplay)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(a => a.Action)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(a => a.Result)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.DeviceDisplay).HasMaxLength(256);
        builder.Property(a => a.TargetType).HasMaxLength(64);
        builder.Property(a => a.TargetId).HasMaxLength(256);
        builder.Property(a => a.TargetDisplay).HasMaxLength(256);
        builder.Property(a => a.FailureReason).HasMaxLength(1024);
        builder.Property(a => a.RequiredPermission).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(512);
        builder.Property(a => a.CorrelationId).HasMaxLength(128);

        // jsonb rather than text: lets operators query inside a state snapshot
        // (e.g. every change where new_state->>'accountType' = 'Administrator')
        // without a schema migration per audited action type.
        builder.Property(a => a.PreviousState).HasColumnType("jsonb");
        builder.Property(a => a.NewState).HasColumnType("jsonb");

        // Npgsql maps System.Net.IPAddress to the native inet type directly - no
        // value converter, which would defeat the point by storing text. inet
        // supports subnet containment queries such as source_ip << '10.0.0.0/8',
        // useful when investigating activity from a particular network.
        builder.Property(a => a.SourceIp)
            .HasColumnType("inet");

        builder.HasOne<Domain.Identity.Organization>()
            .WithMany()
            .HasForeignKey(a => a.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // The dominant query is "recent activity for this organization", so the
        // index is ordered descending on time to match.
        builder.HasIndex(a => new { a.OrganizationId, a.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_log_entries_organization_id_occurred_at");

        builder.HasIndex(a => new { a.OrganizationId, a.Action, a.OccurredAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_audit_log_entries_organization_id_action_occurred_at");

        builder.HasIndex(a => new { a.ActorId, a.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_log_entries_actor_id_occurred_at");

        builder.HasIndex(a => new { a.DeviceId, a.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_log_entries_device_id_occurred_at");

        // Supports alerting on denials without scanning the whole table.
        builder.HasIndex(a => new { a.OrganizationId, a.OccurredAt })
            .HasFilter("result <> 'Success'")
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_log_entries_failures");

        builder.HasIndex(a => a.CorrelationId)
            .HasDatabaseName("ix_audit_log_entries_correlation_id");
    }
}
