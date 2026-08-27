using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class LocalAdminElevationConfiguration : IEntityTypeConfiguration<LocalAdminElevation>
{
    /// <summary>
    /// The states in which an elevation still holds a claim on an account.
    /// </summary>
    /// <remarks>
    /// Written as a SQL literal rather than generated from the enum, because it
    /// becomes part of a database index definition: a value that changed silently
    /// when someone reordered the enum would change what the constraint protects
    /// without anyone reviewing it. Kept beside the index it filters so the two
    /// are read together.
    /// </remarks>
    private const string LiveStates = "state IN ('Requested', 'Approved', 'Active')";

    public void Configure(EntityTypeBuilder<LocalAdminElevation> builder)
    {
        builder.ToTable("local_admin_elevations");

        builder.HasKey(e => e.Id);

        // 184 characters is the documented maximum length of a Windows SID in
        // string form; the username is presentation only and matches the width
        // used for local accounts elsewhere.
        builder.Property(e => e.TargetSid).HasMaxLength(184).IsRequired();
        builder.Property(e => e.TargetUsername).HasMaxLength(256).IsRequired();

        builder.Property(e => e.Justification).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.RequestedByDisplay).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ApprovedByDisplay).HasMaxLength(256);
        builder.Property(e => e.DecisionNote).HasMaxLength(1000);
        builder.Property(e => e.FailureReason).HasMaxLength(1000);

        // Text, like every other enum in the schema: reordering a member can then
        // never silently reinterpret stored history, and a state column that reads
        // "Active" is legible in a psql session during an incident.
        builder.Property(e => e.State).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(e => e.RequestedAt).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // The console's per-device view.
        builder.HasIndex(e => new { e.DeviceId, e.State })
            .HasDatabaseName("ix_local_admin_elevations_device_state");

        // The sweeper's access path: find authorizations whose deadline has passed.
        builder.HasIndex(e => new { e.State, e.ExpiresAt })
            .HasDatabaseName("ix_local_admin_elevations_state_expires");

        // ---------------------------------------------------------------
        // The uniqueness guarantee.
        //
        // At most one elevation may hold a claim on an account at a time. The
        // domain's WouldConflict check reads a snapshot and is therefore only a
        // courtesy: two concurrent requests can both observe no live elevation
        // and both pass it. This index is the actual protection -- the loser of
        // that race fails on insert instead of quietly creating a second window
        // with its own deadline.
        //
        // Partial, so it constrains only the states that still authorize
        // something. Without the filter an account could never be elevated twice
        // in its lifetime, because a finished elevation from last month would
        // block a new one today.
        // ---------------------------------------------------------------
        builder.HasIndex(e => new { e.DeviceId, e.TargetSid })
            .IsUnique()
            .HasFilter(LiveStates)
            .HasDatabaseName("ux_local_admin_elevations_live_per_account");

        // Cascade from the device: an elevation is meaningless once the endpoint
        // it applies to is gone.
        builder.HasOne<Domain.Devices.Device>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deliberately NO foreign key to device_local_users. Inventory is
        // replaced wholesale on every report and is pruned; the audit question
        // "who was given administrator rights on that machine, and when" has to
        // stay answerable after the account row has gone. The SID is carried as a
        // value for the same reason.
    }
}
