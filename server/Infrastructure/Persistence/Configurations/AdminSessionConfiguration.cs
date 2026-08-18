using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AdminSessionConfiguration : IEntityTypeConfiguration<AdminSession>
{
    public void Configure(EntityTypeBuilder<AdminSession> builder)
    {
        builder.ToTable("admin_sessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.PlatformUserId).IsRequired();

        builder.Property(s => s.TokenHash)
            .HasMaxLength(AdminSession.TokenHashLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(s => s.SecurityStampSnapshot)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.ExpiresAt).IsRequired();
        builder.Property(s => s.LastActivityAt).IsRequired();
        builder.Property(s => s.SourceIp).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(512);

        builder.HasOne(s => s.PlatformUser)
            .WithMany()
            .HasForeignKey(s => s.PlatformUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every authenticated request resolves the session by token hash.
        builder.HasIndex(s => s.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_admin_sessions_token_hash");

        // Cleanup job scans by expiry.
        builder.HasIndex(s => s.ExpiresAt)
            .HasDatabaseName("ix_admin_sessions_expires_at");
    }
}
