using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class BitLockerEscrowAttemptConfiguration
    : IEntityTypeConfiguration<BitLockerEscrowAttempt>
{
    public void Configure(EntityTypeBuilder<BitLockerEscrowAttempt> builder)
    {
        builder.ToTable("bitlocker_escrow_attempts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.DeviceId).IsRequired();
        builder.Property(a => a.VolumeDeviceIdentifier).HasMaxLength(256).IsRequired();
        builder.Property(a => a.KeyProtectorId).HasMaxLength(64).IsRequired();

        builder.Property(a => a.State).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.LastFailure).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(a => a.AttemptCount).IsRequired();
        builder.Property(a => a.FirstSeenAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(a => a.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One retry state per protector, unconditionally unique -- unlike the
        // escrow table's index, which is filtered on is_active because superseded
        // rows accumulate there. Nothing accumulates here: a protector has exactly
        // one current position in the schedule, and a new protector is a new row.
        builder.HasIndex(a => new { a.DeviceId, a.VolumeDeviceIdentifier, a.KeyProtectorId })
            .IsUnique()
            .HasDatabaseName("ux_bitlocker_escrow_attempts_protector");

        // Serves the due-work query: the agent asks what is owed to it now, and
        // that filters on state and next_attempt_at together.
        builder.HasIndex(a => new { a.State, a.NextAttemptAt })
            .HasDatabaseName("ix_bitlocker_escrow_attempts_due");
    }
}
