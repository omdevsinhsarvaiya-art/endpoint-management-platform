using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// How elevations are stored, and the constraint that actually protects them.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing test here is the partial unique index. Every other layer's
/// uniqueness check reads a snapshot and can be beaten by a concurrent request,
/// so the constraint is the only thing that can genuinely prevent two live
/// authorizations for one account. These tests reach past the service and insert
/// directly, because a test that went through the service would pass on the
/// service's own check and prove nothing about the database.
/// </para>
/// <para>
/// Equally important is that the index is <em>partial</em>. Without the filter an
/// account could be elevated exactly once ever, because a record from months ago
/// would still collide.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class LocalAdminElevationPersistenceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private const string MachineSid = "S-1-5-21-4-4-4";
    private const string TargetSid = MachineSid + "-1001";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static async Task<Organization> EnsureOrganizationAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db)
    {
        var existing = await db.Organizations.FirstOrDefaultAsync(o => o.Slug == "test-org");
        if (existing is not null)
        {
            return existing;
        }

        var org = new Organization("Test Organization", "test-org");
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task<Device> SeedDeviceAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db, Guid organizationId)
    {
        var token = new EnrollmentToken(
            organizationId, $"lae-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", Now.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, "LAE-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, Now);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static LocalAdminElevation Approved(Guid orgId, Guid deviceId, string sid = TargetSid) =>
        LocalAdminElevation.RequestAndApprove(
            orgId, deviceId, sid, "sarah", "Signed vendor driver.",
            Guid.CreateVersion7(), "admin@test", TimeSpan.FromHours(1), Now);

    // ---- round trip --------------------------------------------------------

    [Fact]
    public async Task An_elevation_round_trips_with_its_state_as_text()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        var elevation = Approved(org.Id, device.Id);
        db.LocalAdminElevations.Add(elevation);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();

        var stored = await db.LocalAdminElevations.AsNoTracking().SingleAsync(e => e.Id == elevation.Id);

        stored.State.ShouldBe(LocalAdminElevationState.Approved);
        stored.TargetSid.ShouldBe(TargetSid);
        stored.TargetUsername.ShouldBe("sarah");
        stored.ExpiresAt.ShouldNotBeNull();
        stored.Justification.ShouldBe("Signed vendor driver.");

        // Stored as text, not as an ordinal: reordering the enum can then never
        // silently reinterpret stored history, and the column is legible in psql.
        var raw = await db.Database
            .SqlQuery<string>(
                $"""select state as "Value" from endpoint_platform.local_admin_elevations where id = {elevation.Id}""")
            .SingleAsync();

        raw.ShouldBe("Approved");
    }

    [Fact]
    public async Task Every_lifecycle_state_persists_and_reads_back()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        var revoked = Approved(org.Id, device.Id, MachineSid + "-2001");
        revoked.TryRevoke(Guid.CreateVersion7(), "admin", "done", Now.AddMinutes(5));

        var expired = Approved(org.Id, device.Id, MachineSid + "-2002");
        expired.TryExpire(expired.ExpiresAt!.Value.AddSeconds(1));

        var failed = Approved(org.Id, device.Id, MachineSid + "-2003");
        failed.TryMarkFailed("access denied", Now);

        db.LocalAdminElevations.AddRange(revoked, expired, failed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var states = await db.LocalAdminElevations.AsNoTracking()
            .Where(e => e.DeviceId == device.Id)
            .Select(e => e.State)
            .ToListAsync();

        states.ShouldBe(
            [LocalAdminElevationState.Revoked, LocalAdminElevationState.Expired, LocalAdminElevationState.Failed],
            ignoreOrder: true);
    }

    // ---- the constraint ----------------------------------------------------

    /// <summary>
    /// Two live elevations for one account are refused by the database.
    /// </summary>
    /// <remarks>
    /// Inserted directly, bypassing every application-level check, because the
    /// question is whether the constraint exists -- not whether the service
    /// remembers to look.
    /// </remarks>
    [Fact]
    public async Task The_database_refuses_a_second_live_elevation_for_the_same_account()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        db.LocalAdminElevations.Add(Approved(org.Id, device.Id));
        await db.SaveChangesAsync();

        db.LocalAdminElevations.Add(Approved(org.Id, device.Id));

        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var pg = ex.InnerException.ShouldBeOfType<PostgresException>();
        pg.SqlState.ShouldBe("23505");
        pg.ConstraintName.ShouldBe("ux_local_admin_elevations_live_per_account");
    }

    [Theory]
    [InlineData(LocalAdminElevationState.Requested)]
    [InlineData(LocalAdminElevationState.Approved)]
    public async Task Every_live_state_participates_in_the_constraint(LocalAdminElevationState state)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        var sid = $"{MachineSid}-{(int)state + 3000}";

        var first = state == LocalAdminElevationState.Requested
            ? LocalAdminElevation.Request(
                org.Id, device.Id, sid, "sarah", "why", Guid.CreateVersion7(), "admin", Now)
            : Approved(org.Id, device.Id, sid);

        db.LocalAdminElevations.Add(first);
        await db.SaveChangesAsync();

        db.LocalAdminElevations.Add(LocalAdminElevation.Request(
            org.Id, device.Id, sid, "sarah", "again", Guid.CreateVersion7(), "admin", Now));

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// The constraint is partial: a finished elevation blocks nothing.
    /// </summary>
    [Theory]
    [InlineData(LocalAdminElevationState.Revoked)]
    [InlineData(LocalAdminElevationState.Expired)]
    [InlineData(LocalAdminElevationState.Failed)]
    [InlineData(LocalAdminElevationState.Rejected)]
    public async Task A_terminal_elevation_does_not_block_a_new_one(LocalAdminElevationState terminal)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        var sid = $"{MachineSid}-{(int)terminal + 4000}";
        var actor = Guid.CreateVersion7();

        LocalAdminElevation finished;
        if (terminal == LocalAdminElevationState.Rejected)
        {
            finished = LocalAdminElevation.Request(org.Id, device.Id, sid, "sarah", "why", actor, "admin", Now);
            finished.Reject(actor, "admin", null, Now);
        }
        else
        {
            finished = Approved(org.Id, device.Id, sid);
            switch (terminal)
            {
                case LocalAdminElevationState.Revoked:
                    finished.TryRevoke(actor, "admin", null, Now.AddMinutes(1));
                    break;
                case LocalAdminElevationState.Expired:
                    finished.TryExpire(finished.ExpiresAt!.Value.AddSeconds(1));
                    break;
                default:
                    finished.TryMarkFailed("nope", Now);
                    break;
            }
        }

        db.LocalAdminElevations.Add(finished);
        await db.SaveChangesAsync();

        // A fresh elevation for the same account must be allowed.
        db.LocalAdminElevations.Add(LocalAdminElevation.Request(
            org.Id, device.Id, sid, "sarah", "later", actor, "admin", Now.AddDays(1)));

        await Should.NotThrowAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Different_accounts_and_devices_do_not_collide()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var deviceA = await SeedDeviceAsync(db, org.Id);
        var deviceB = await SeedDeviceAsync(db, org.Id);

        db.LocalAdminElevations.Add(Approved(org.Id, deviceA.Id, MachineSid + "-5001"));
        db.LocalAdminElevations.Add(Approved(org.Id, deviceA.Id, MachineSid + "-5002"));
        db.LocalAdminElevations.Add(Approved(org.Id, deviceB.Id, MachineSid + "-5001"));

        await Should.NotThrowAsync(() => db.SaveChangesAsync());
    }

    // ---- foreign keys ------------------------------------------------------

    [Fact]
    public async Task Removing_the_device_cascades_to_its_elevations()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        db.LocalAdminElevations.Add(Approved(org.Id, device.Id, MachineSid + "-6001"));
        await db.SaveChangesAsync();

        db.Devices.Remove(device);
        await db.SaveChangesAsync();

        (await db.LocalAdminElevations.CountAsync(e => e.DeviceId == device.Id)).ShouldBe(0);
    }

    /// <summary>
    /// An elevation survives the account row being pruned.
    /// </summary>
    /// <remarks>
    /// There is deliberately no foreign key to <c>device_local_users</c>.
    /// Inventory is replaced wholesale on every report, so a key there would
    /// delete the record of who was given administrator rights the moment the
    /// account stopped being reported -- and that is exactly the question an
    /// auditor asks months later.
    /// </remarks>
    [Fact]
    public async Task An_elevation_outlives_the_local_account_row_it_refers_to()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await EnsureOrganizationAsync(db);
        var device = await SeedDeviceAsync(db, org.Id);

        var sid = MachineSid + "-7001";
        var account = new DeviceLocalUser(
            device.Id, sid, "sarah", "Sarah", null, true, true, true, Now, false, Now);
        db.DeviceLocalUsers.Add(account);

        var elevation = Approved(org.Id, device.Id, sid);
        db.LocalAdminElevations.Add(elevation);
        await db.SaveChangesAsync();

        // Inventory replaced: the account is gone.
        db.DeviceLocalUsers.Remove(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.LocalAdminElevations.AsNoTracking().SingleAsync(e => e.Id == elevation.Id);
        stored.TargetSid.ShouldBe(sid);
        stored.TargetUsername.ShouldBe("sarah");
    }
}
