using EndpointPlatform.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AgentReleaseConfiguration : IEntityTypeConfiguration<AgentRelease>
{
    public void Configure(EntityTypeBuilder<AgentRelease> builder)
    {
        builder.ToTable("agent_releases");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Version).HasMaxLength(32).IsRequired();
        builder.Property(r => r.Platform).HasMaxLength(32).IsRequired();
        builder.Property(r => r.Architecture).HasMaxLength(16).IsRequired();
        builder.Property(r => r.FileName).HasMaxLength(128).IsRequired();
        builder.Property(r => r.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(r => r.SignerSubject).HasMaxLength(256);
        builder.Property(r => r.ReleaseNotes).HasMaxLength(4000);

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(r => r.CreatedByUserId).IsRequired();
        builder.Property(r => r.CreatedByDisplay).HasMaxLength(320).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // One row per build of a given target: uploading 1.1.0 for windows/x64
        // twice is a mistake, not a second release.
        builder.HasIndex(r => new { r.Platform, r.Architecture, r.Version })
            .IsUnique()
            .HasDatabaseName("ix_agent_releases_platform_architecture_version");

        // "Latest published for windows/x64" is the hot query — from the
        // dashboard, from every device-list row, and from agents cross-checking
        // an update task.
        builder.HasIndex(r => new { r.Platform, r.Architecture, r.Status })
            .HasDatabaseName("ix_agent_releases_platform_architecture_status");
    }
}
