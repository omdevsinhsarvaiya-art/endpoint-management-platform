using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class BitLockerRecoveryEscrowConfiguration
    : IEntityTypeConfiguration<BitLockerRecoveryEscrow>
{
    public void Configure(EntityTypeBuilder<BitLockerRecoveryEscrow> builder)
    {
        builder.ToTable("bitlocker_recovery_escrows");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizationId).IsRequired();
        builder.Property(e => e.DeviceId).IsRequired();
        builder.Property(e => e.VolumeDeviceIdentifier).HasMaxLength(256).IsRequired();
        builder.Property(e => e.KeyProtectorId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.DriveLetter).HasMaxLength(8);

        // The AES-GCM envelope, base64. Never indexed and never selected into a
        // projection that leaves the service layer.
        builder.Property(e => e.SealedRecoveryPassword).HasMaxLength(4096).IsRequired();

        builder.Property(e => e.KeyVersion).IsRequired();

        // Nullable since automatic escrow: an agent-originated row has no human
        // actor. Existing manual rows are unaffected -- they already carry one.
        builder.Property(e => e.EscrowedByUserId);

        builder.Property(e => e.EscrowedByDisplay).HasMaxLength(320).IsRequired();

        // Stored as text so a database dump stays readable and so adding a scheme
        // or an origin later cannot renumber the existing ones.
        builder.Property(e => e.Origin)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.SealScheme)
            .HasMaxLength(BitLockerSealScheme.MaxLength)
            .IsRequired();
        builder.Property(e => e.EscrowedAt).IsRequired();
        builder.Property(e => e.IsActive).IsRequired();
        builder.Property(e => e.RevealedCount).IsRequired();
        builder.Property(e => e.DeletedByDisplay).HasMaxLength(320);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // SupersededById is a plain column, deliberately NOT a foreign key.
        //
        // A self-referencing FK creates a constraint cycle with the partial unique
        // index below: the FK needs the replacement row to exist before the old row
        // can point at it, while the index needs the old row to be inactive before
        // the replacement can be inserted. Nothing can satisfy both in one
        // transaction without deferring one of them, and PostgreSQL cannot defer a
        // partial index.
        //
        // The FK bought little here in any case: escrow rows are never hard-deleted
        // -- deletion overwrites the ciphertext and keeps the row -- so the referent
        // always exists. The chain is navigational history, not an integrity claim.
        builder.HasIndex(e => e.SupersededById)
            .HasDatabaseName("ix_bitlocker_recovery_escrows_superseded_by");

        builder.HasIndex(e => e.DeviceId)
            .HasDatabaseName("ix_bitlocker_recovery_escrows_device_id");

        // The authoritative guarantee that one protector has one live key.
        //
        // Filtered on is_active so superseded and deleted records stay in the
        // table without blocking the next escrow. The domain checks for a
        // conflict first, but two concurrent requests both pass that check --
        // this index is what actually separates them, and the service catches
        // 23505 and reports a conflict.
        builder.HasIndex(e => new { e.DeviceId, e.VolumeDeviceIdentifier, e.KeyProtectorId })
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_bitlocker_recovery_escrows_active");
    }
}
