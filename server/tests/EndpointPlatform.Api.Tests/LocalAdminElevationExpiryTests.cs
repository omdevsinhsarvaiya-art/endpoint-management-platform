using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Expiry of administrator elevations, against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The property these are really about: <b>the sweeper is not the authorization
/// boundary</b>. Access ends at the deadline whether or not the sweeper has run,
/// so the tests check what the server would <em>publish</em> to an endpoint at a
/// given instant, not merely what label a row carries. A design where expiry
/// depended on a background process running on time would mean a paused
/// container silently extended someone's administrator rights.
/// </para>
/// <para>
/// Time is supplied by a controllable clock injected into the scope, so deadlines
/// are crossed deliberately rather than by waiting.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class LocalAdminElevationExpiryTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string Machine = "S-1-5-21-5-5-5";

    private static readonly DateTimeOffset Base = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A clock the test moves, so deadlines are crossed on purpose.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>
    /// A service over the shared database with a clock this test controls.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than resolved, because the point is to drive time.
    /// The DbContext and AuditWriter still come from the real container, so the
    /// database behaviour under test is the production behaviour.
    /// </remarks>
    private (LocalAdminElevationService Service, Clock Clock, IServiceScope Scope) BuildService(
        DateTimeOffset? at = null)
    {
        var scope = _fixture.Factory.Services.CreateScope();
        var clock = new Clock(at ?? Base);

        var service = new LocalAdminElevationService(
            scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.EndpointPlatformDbContext>(),
            scope.ServiceProvider.GetRequiredService<Infrastructure.Tasks.DeviceTaskService>(),
            scope.ServiceProvider.GetRequiredService<Infrastructure.Auditing.AuditWriter>(),
            clock,
            scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<LocalAdminElevationService>>());

        return (service, clock, scope);
    }

    private async Task<(Guid DeviceId, Guid OrganizationId)> SeedDeviceAsync(params string[] sids)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"exp-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            org.Id, "EXP-PC", "m-" + Guid.CreateVersion7().ToString("N"),
            "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        foreach (var sid in sids)
        {
            db.DeviceLocalUsers.Add(new DeviceLocalUser(
                device.Id, sid, "user" + sid[^4..], null, null, true, true, true,
                DateTimeOffset.UtcNow, false, DateTimeOffset.UtcNow));
        }

        await db.SaveChangesAsync();
        return (device.Id, org.Id);
    }

    private async Task<Guid> SeedElevationAsync(
        Guid orgId, Guid deviceId, string sid, TimeSpan duration, bool activate = false)
    {
        await using var db = _fixture.CreateDbContext();

        var elevation = LocalAdminElevation.RequestAndApprove(
            orgId, deviceId, sid, "user", "Test elevation.",
            Guid.CreateVersion7(), "admin@test", duration, Base);

        if (activate)
        {
            elevation.TryActivate(Base.AddMinutes(1)).ShouldBeTrue();
        }

        db.LocalAdminElevations.Add(elevation);
        await db.SaveChangesAsync();
        return elevation.Id;
    }

    private async Task<LocalAdminElevationState> StateOfAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return (await db.LocalAdminElevations.AsNoTracking().SingleAsync(e => e.Id == id)).State;
    }

    private async Task<int> ExpiryAuditCountAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .CountAsync(a => a.Action == "localuser.elevation.expired" && a.TargetId == id.ToString());
    }

    // ---- the deadline ------------------------------------------------------

    [Fact]
    public async Task An_approved_elevation_expires_at_its_deadline()
    {
        var sid = Machine + "-1001";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            (await service.SweepExpiredAsync(100)).ShouldBe(0);
            (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Approved);

            clock.Advance(TimeSpan.FromHours(2));
            (await service.SweepExpiredAsync(100)).ShouldBe(1);
        }

        (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Expired);
    }

    [Fact]
    public async Task An_active_elevation_expires_at_its_deadline()
    {
        var sid = Machine + "-1002";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1), activate: true);

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(2));
            (await service.SweepExpiredAsync(100)).ShouldBe(1);
        }

        (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Expired);
    }

    /// <summary>
    /// The boundary itself: at exactly the deadline the elevation is over.
    /// </summary>
    /// <remarks>
    /// <c>IsLive</c> uses a strict comparison, so the instant the deadline is
    /// reached the authorization has ended rather than having one final moment.
    /// </remarks>
    [Fact]
    public async Task At_exactly_the_deadline_the_elevation_is_no_longer_live()
    {
        var sid = Machine + "-1003";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var (service, _, scope) = BuildService();
        using (scope)
        {
            (await service.BuildDesiredElevationsAsync(deviceId, Base.AddHours(1).AddTicks(-1)))
                .Count.ShouldBe(1);

            (await service.BuildDesiredElevationsAsync(deviceId, Base.AddHours(1)))
                .ShouldBeEmpty();
        }
    }

    // ---- the sweeper is not the boundary -----------------------------------

    /// <summary>
    /// A lapsed elevation stops being published before any sweep runs.
    /// </summary>
    /// <remarks>
    /// The most important test here. The row still says Approved -- nothing has
    /// swept it -- and the server already refuses to tell the endpoint about it.
    /// That is what makes a stalled sweeper a cosmetic problem rather than a
    /// security one.
    /// </remarks>
    [Fact]
    public async Task A_lapsed_elevation_is_not_published_even_before_the_sweeper_runs()
    {
        var sid = Machine + "-1004";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromMinutes(30));

        var (service, _, scope) = BuildService();
        using (scope)
        {
            (await service.BuildDesiredElevationsAsync(deviceId, Base.AddHours(3))).ShouldBeEmpty();
        }

        // Deliberately unswept, and still not published.
        (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Approved);
    }

    [Fact]
    public async Task Time_spent_offline_does_not_extend_authorization()
    {
        var sid = Machine + "-1005";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var (service, _, scope) = BuildService();
        using (scope)
        {
            // The endpoint was unreachable for the whole window and reconnects
            // long afterwards. It is told nothing is authorized.
            (await service.BuildDesiredElevationsAsync(deviceId, Base.AddDays(3))).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task An_expired_elevation_is_absent_from_the_desired_set_after_sweeping()
    {
        var sid = Machine + "-1006";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(2));
            await service.SweepExpiredAsync(100);

            (await service.BuildDesiredElevationsAsync(deviceId, clock.GetUtcNow())).ShouldBeEmpty();
        }
    }

    // ---- idempotency -------------------------------------------------------

    /// <summary>
    /// Repeated sweeps neither re-transition nor duplicate the audit record.
    /// </summary>
    /// <remarks>
    /// The transition is guarded by the row's expected state in the WHERE clause,
    /// so a second pass updates zero rows and writes nothing. A read-then-write
    /// would record the same expiry twice.
    /// </remarks>
    [Fact]
    public async Task Repeated_sweeps_expire_once_and_audit_once()
    {
        var sid = Machine + "-1007";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(2));

            (await service.SweepExpiredAsync(100)).ShouldBe(1);
            (await service.SweepExpiredAsync(100)).ShouldBe(0);
            (await service.SweepExpiredAsync(100)).ShouldBe(0);
        }

        (await ExpiryAuditCountAsync(id)).ShouldBe(1);
    }

    /// <summary>
    /// Two sweepers running at once expire the elevation exactly once.
    /// </summary>
    /// <remarks>
    /// Each has its own scope and DbContext, as separate instances would. Only
    /// the guarded update can separate them: both read the same candidate, and
    /// only one update matches a row.
    /// </remarks>
    [Fact]
    public async Task Competing_sweepers_produce_one_transition_and_one_audit_entry()
    {
        var sid = Machine + "-1008";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var runs = Enumerable.Range(0, 4).Select(async _ =>
        {
            var (service, clock, scope) = BuildService(Base.AddHours(2));
            using (scope)
            {
                return await service.SweepExpiredAsync(100);
            }
        });

        var results = await Task.WhenAll(runs);

        results.Sum().ShouldBe(1);
        (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Expired);
        (await ExpiryAuditCountAsync(id)).ShouldBe(1);
    }

    // ---- records the sweeper must not touch --------------------------------

    [Theory]
    [InlineData(LocalAdminElevationState.Revoked)]
    [InlineData(LocalAdminElevationState.Rejected)]
    [InlineData(LocalAdminElevationState.Failed)]
    public async Task A_terminal_elevation_is_left_alone(LocalAdminElevationState terminal)
    {
        var sid = $"{Machine}-{2000 + (int)terminal}";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var actor = Guid.CreateVersion7();

        Guid id;
        await using (var db = _fixture.CreateDbContext())
        {
            LocalAdminElevation elevation;
            if (terminal == LocalAdminElevationState.Rejected)
            {
                elevation = LocalAdminElevation.Request(
                    orgId, deviceId, sid, "user", "why", actor, "admin", Base);
                elevation.Reject(actor, "admin", null, Base);
            }
            else
            {
                elevation = LocalAdminElevation.RequestAndApprove(
                    orgId, deviceId, sid, "user", "why", actor, "admin", TimeSpan.FromHours(1), Base);

                if (terminal == LocalAdminElevationState.Revoked)
                {
                    elevation.TryRevoke(actor, "admin", null, Base.AddMinutes(1));
                }
                else
                {
                    elevation.TryMarkFailed("could not apply", Base.AddMinutes(1));
                }
            }

            db.LocalAdminElevations.Add(elevation);
            await db.SaveChangesAsync();
            id = elevation.Id;
        }

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromDays(1));
            await service.SweepExpiredAsync(100);
        }

        (await StateOfAsync(id)).ShouldBe(terminal);
        (await ExpiryAuditCountAsync(id)).ShouldBe(0);
    }

    // ---- several at once ---------------------------------------------------

    [Fact]
    public async Task Several_elevations_on_one_device_expire_independently()
    {
        var shortSid = Machine + "-3001";
        var longSid = Machine + "-3002";
        var (deviceId, orgId) = await SeedDeviceAsync(shortSid, longSid);

        var shortId = await SeedElevationAsync(orgId, deviceId, shortSid, TimeSpan.FromMinutes(30));
        var longId = await SeedElevationAsync(orgId, deviceId, longSid, TimeSpan.FromHours(4));

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(1));
            (await service.SweepExpiredAsync(100)).ShouldBe(1);

            var desired = await service.BuildDesiredElevationsAsync(deviceId, clock.GetUtcNow());
            desired.Select(d => d.Sid).ShouldBe([longSid]);
        }

        (await StateOfAsync(shortId)).ShouldBe(LocalAdminElevationState.Expired);
        (await StateOfAsync(longId)).ShouldBe(LocalAdminElevationState.Approved);
    }

    // ---- reconciliation ----------------------------------------------------

    /// <summary>
    /// After expiry the endpoint's remaining whole state is exactly the
    /// elevations still authorized.
    /// </summary>
    /// <remarks>
    /// Revocation is the absence of an entry rather than a "remove" instruction,
    /// so an endpoint that misses a message still converges from the next set it
    /// receives.
    ///
    /// The sweeper deliberately queues no task -- see the note in
    /// <c>SweepExpiredAsync</c> -- so what is asserted here is what the endpoint
    /// would be told, which is the thing that actually governs it.
    /// </remarks>
    [Fact]
    public async Task After_expiry_the_desired_set_holds_only_what_is_still_authorized()
    {
        var goneSid = Machine + "-4001";
        var keptSid = Machine + "-4002";
        var (deviceId, orgId) = await SeedDeviceAsync(goneSid, keptSid);

        await SeedElevationAsync(orgId, deviceId, goneSid, TimeSpan.FromMinutes(30));
        await SeedElevationAsync(orgId, deviceId, keptSid, TimeSpan.FromHours(4));

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await service.SweepExpiredAsync(100);

            var desired = await service.BuildDesiredElevationsAsync(deviceId, clock.GetUtcNow());

            desired.Select(d => d.Sid).ShouldBe([keptSid]);
            desired.ShouldNotContain(d => d.Sid == goneSid);
        }
    }

    /// <summary>
    /// An expired elevation cannot be resurrected by reconciliation.
    /// </summary>
    [Fact]
    public async Task An_expired_elevation_is_never_published_again()
    {
        var sid = Machine + "-4003";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromMinutes(30));

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await service.SweepExpiredAsync(100);

            // Every subsequent build, at any later time, still publishes nothing.
            (await service.BuildDesiredElevationsAsync(deviceId, clock.GetUtcNow())).ShouldBeEmpty();
            (await service.BuildDesiredElevationsAsync(deviceId, Base.AddDays(7))).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A failed de-elevation leaves authorization Expired, and the still-elevated
    /// account is visible from what the endpoint reports.
    /// </summary>
    /// <remarks>
    /// The decision recorded in M12-1: authorization genuinely ended, so the
    /// record expires regardless. Whether the account is still an administrator
    /// in fact is answered by <c>DeviceLocalUser.IsLocalAdministrator</c>, which
    /// the agent already reports -- no extra persistence, and the pair reads as
    /// drift exactly like the USB console's Drifted state.
    /// </remarks>
    [Fact]
    public async Task A_failed_de_elevation_leaves_the_record_expired_and_the_drift_visible()
    {
        var sid = Machine + "-4004";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromMinutes(30), activate: true);

        var (service, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await service.SweepExpiredAsync(100);
        }

        // The endpoint reports it is still an administrator: de-elevation failed.
        await using (var db = _fixture.CreateDbContext())
        {
            var account = await db.DeviceLocalUsers.SingleAsync(u => u.DeviceId == deviceId && u.Sid == sid);
            db.DeviceLocalUsers.Remove(account);
            db.DeviceLocalUsers.Add(new DeviceLocalUser(
                deviceId, sid, "user", null, null, true, true, true,
                DateTimeOffset.UtcNow, isLocalAdministrator: true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        // Authorization is over...
        (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Expired);

        // ...and the drift is visible without any new table or column.
        await using var check = _fixture.CreateDbContext();
        var stillAdmin = await check.DeviceLocalUsers.AsNoTracking()
            .SingleAsync(u => u.DeviceId == deviceId && u.Sid == sid);

        stillAdmin.IsLocalAdministrator.ShouldBeTrue();
    }

    // ---- durability --------------------------------------------------------

    /// <summary>
    /// Expiry state survives a restart, because it is a row rather than a timer.
    /// </summary>
    [Fact]
    public async Task Expiry_state_survives_a_new_service_instance()
    {
        var sid = Machine + "-5001";
        var (deviceId, orgId) = await SeedDeviceAsync(sid);
        var id = await SeedElevationAsync(orgId, deviceId, sid, TimeSpan.FromHours(1));

        var (first, clock, scope) = BuildService();
        using (scope)
        {
            clock.Advance(TimeSpan.FromHours(2));
            await first.SweepExpiredAsync(100);
        }

        // A brand-new service, as after a restart. Nothing is carried in memory.
        var (second, _, scope2) = BuildService(Base.AddHours(3));
        using (scope2)
        {
            (await second.SweepExpiredAsync(100)).ShouldBe(0);
            (await second.BuildDesiredElevationsAsync(deviceId, Base.AddHours(3))).ShouldBeEmpty();
        }

        (await StateOfAsync(id)).ShouldBe(LocalAdminElevationState.Expired);
    }
}
