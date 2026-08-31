using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AgentCredentialConfiguration : IEntityTypeConfiguration<AgentCredential>
{
    public void Configure(EntityTypeBuilder<AgentCredential> builder)
    {
        builder.ToTable("agent_credentials");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.DeviceId).IsRequired();

        builder.Property(c => c.KeyId)
            .HasMaxLength(AgentCredential.KeyIdLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(c => c.SecretHash)
            .HasMaxLength(AgentCredential.SecretHashLength)
            .IsFixedLength()
            .IsRequired();

        // Nullable by design: null means this credential predates automatic escrow
        // (or was issued without pinning) and the device is not eligible for it.
        builder.Property(c => c.SealingKeyFingerprint).HasMaxLength(64);

        builder.Property(c => c.IssuedAt).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Devices.Device>()
            .WithMany()
            .HasForeignKey(c => c.DeviceId)
            // Credential history is audit-relevant; devices are retired, not deleted.
            .OnDelete(DeleteBehavior.Restrict);

        // Authentication resolves the credential by key id.
        builder.HasIndex(c => c.KeyId)
            .IsUnique()
            .HasDatabaseName("ix_agent_credentials_key_id");

        // "The active credential for device X" - partial index keeps it tiny.
        builder.HasIndex(c => c.DeviceId)
            .HasFilter("revoked_at IS NULL")
            .IsUnique()
            .HasDatabaseName("ix_agent_credentials_device_id_active");
    }
}
