using EndpointPlatform.Domain.Peripherals;

namespace EndpointPlatform.Domain.Tests.Peripherals;

/// <summary>
/// The USB access invariants, asserted at the level that owns them.
/// </summary>
/// <remarks>
/// These are the rules the whole feature rests on: storage starts restricted,
/// only storage can be granted, grants are read-only and time-boxed, and expiry
/// is decided by the clock rather than by a sweep having run. A change that
/// breaks one of them should fail here, loudly, rather than at three in the
/// morning on somebody's laptop.
/// </remarks>
public sealed class UsbDeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static UsbDevice NewDevice(UsbDeviceClass deviceClass = UsbDeviceClass.Storage) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            @"USB\VID_0781&PID_5581\ABC123",
            deviceClass,
            "0781",
            "5581",
            "ABC123",
            "SanDisk",
            "Cruzer Fit",
            @"USB\VID_0781&PID_5581",
            Now);

    [Fact]
    public void Storage_is_restricted_the_moment_it_is_first_seen()
    {
        var device = NewDevice();

        device.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        device.PolicyExpiresAt.ShouldBeNull();
        device.HasLiveGrant(Now).ShouldBeFalse();
    }

    [Fact]
    public void A_grant_is_live_until_its_deadline_and_not_a_moment_after()
    {
        var device = NewDevice();
        device.GrantReadOnly(Now.AddHours(2), Now);

        device.Policy.ShouldBe(UsbStoragePolicy.ReadOnly);
        device.HasLiveGrant(Now).ShouldBeTrue();
        device.HasLiveGrant(Now.AddHours(2).AddTicks(-1)).ShouldBeTrue();

        // No sweep has run. The grant is over anyway, because liveness is a
        // question about the clock, not about stored state.
        device.HasLiveGrant(Now.AddHours(2)).ShouldBeFalse();
        device.HasLiveGrant(Now.AddDays(1)).ShouldBeFalse();
    }

    [Fact]
    public void Non_storage_devices_cannot_be_granted_access_at_all()
    {
        foreach (var deviceClass in new[]
                 {
                     UsbDeviceClass.Keyboard, UsbDeviceClass.Mouse, UsbDeviceClass.Hub,
                     UsbDeviceClass.NetworkAdapter, UsbDeviceClass.Other, UsbDeviceClass.Unknown,
                 })
        {
            var device = NewDevice(deviceClass);

            Should.Throw<InvalidOperationException>(() => device.GrantReadOnly(Now.AddHours(1), Now));
        }
    }

    [Fact]
    public void A_grant_cannot_be_backdated_into_an_already_expired_one()
    {
        var device = NewDevice();

        Should.Throw<ArgumentOutOfRangeException>(() => device.GrantReadOnly(Now.AddSeconds(-1), Now));
        Should.Throw<ArgumentOutOfRangeException>(() => device.GrantReadOnly(Now, Now));
    }

    /// <summary>
    /// Unplugging must not be a way to shed a restriction.
    /// </summary>
    /// <remarks>
    /// If disconnection reset policy, the bypass would be trivial: pull the
    /// stick out, push it back in, and arrive as a device with no history.
    /// </remarks>
    [Fact]
    public void Policy_survives_a_disconnect_and_reconnect()
    {
        var device = NewDevice();
        device.GrantReadOnly(Now.AddHours(2), Now);

        device.Disconnected(Now.AddMinutes(5));
        device.IsConnected.ShouldBeFalse();
        device.Policy.ShouldBe(UsbStoragePolicy.ReadOnly);

        device.Seen(UsbDeviceClass.Storage, null, null, null, Now.AddMinutes(10));
        device.IsConnected.ShouldBeTrue();
        device.Policy.ShouldBe(UsbStoragePolicy.ReadOnly);
        device.HasLiveGrant(Now.AddMinutes(10)).ShouldBeTrue();

        // ...and the deadline is still the original one, not extended by the replug.
        device.HasLiveGrant(Now.AddHours(3)).ShouldBeFalse();
    }

    [Fact]
    public void Restricting_is_idempotent_and_clears_the_deadline()
    {
        var device = NewDevice();
        device.GrantReadOnly(Now.AddHours(1), Now);

        device.Restrict();
        device.Restrict();

        device.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        device.PolicyExpiresAt.ShouldBeNull();
    }

    /// <summary>
    /// Desired state and enforced state must stay separately observable.
    /// </summary>
    /// <remarks>
    /// Collapsing them is the mistake that makes a console lie: a device the
    /// administrator has restricted, on a machine that is offline, would render
    /// as "Restricted" with nothing to indicate the machine has never been told.
    /// </remarks>
    [Fact]
    public void Enforcement_is_tracked_apart_from_the_decision()
    {
        var device = NewDevice();

        device.EnforcedPolicy.ShouldBeNull();
        device.IsPolicyEnforced.ShouldBeFalse();

        device.ReportEnforcement(UsbStoragePolicy.Restricted, null, Now);
        device.IsPolicyEnforced.ShouldBeTrue();

        // The console grants access; the endpoint has not confirmed it yet.
        device.GrantReadOnly(Now.AddHours(1), Now);
        device.IsPolicyEnforced.ShouldBeFalse();

        device.ReportEnforcement(UsbStoragePolicy.ReadOnly, null, Now.AddSeconds(5));
        device.IsPolicyEnforced.ShouldBeTrue();

        // A reported failure means not enforced, whatever policy it claims.
        device.ReportEnforcement(UsbStoragePolicy.ReadOnly, "access denied", Now.AddSeconds(10));
        device.IsPolicyEnforced.ShouldBeFalse();
    }

    [Fact]
    public void A_device_without_a_serial_is_stored_without_one_rather_than_a_placeholder()
    {
        var device = new UsbDevice(
            Guid.CreateVersion7(), Guid.CreateVersion7(),
            @"USB\VID_1234&PID_5678\7&2f3c1b2&0&2",
            UsbDeviceClass.Storage,
            "1234", "5678",
            serialNumber: null,
            manufacturer: null, product: null, hardwareIds: null,
            Now);

        device.SerialNumber.ShouldBeNull();
    }
}

public sealed class UsbAccessRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static UsbAccessRequest Grant(TimeSpan duration) =>
        UsbAccessRequest.GrantByAdministrator(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            @"USB\VID_0781&PID_5581\ABC123",
            "Vendor delivered firmware on a stick.",
            Guid.CreateVersion7(), "admin@company.local",
            duration, Now);

    [Fact]
    public void An_administrator_grant_records_the_approver_and_a_deadline()
    {
        var request = Grant(TimeSpan.FromHours(2));

        request.Status.ShouldBe(UsbAccessRequestStatus.Approved);
        request.Source.ShouldBe(UsbAccessRequestSource.Administrator);
        request.DecidedByDisplay.ShouldBe("admin@company.local");
        request.ExpiresAt.ShouldBe(Now.AddHours(2));
        request.IsLive(Now).ShouldBeTrue();
        request.IsLive(Now.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void Grant_duration_is_bounded_at_both_ends()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Grant(TimeSpan.FromMinutes(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => Grant(TimeSpan.FromDays(7)));

        Should.NotThrow(() => Grant(UsbAccessRequest.MinimumDuration));
        Should.NotThrow(() => Grant(UsbAccessRequest.MaximumDuration));
    }

    /// <summary>
    /// Revocation must close the window as well as change the status.
    /// </summary>
    /// <remarks>
    /// Two different queries answer "did this grant apply at time T": one reads
    /// the status, one reads the window. If revoking moved only the status, a
    /// window-based query — including the one that builds the policy sent to
    /// endpoints — would still consider the grant live until its original
    /// deadline.
    /// </remarks>
    [Fact]
    public void Revoking_moves_the_deadline_to_now_as_well_as_the_status()
    {
        var request = Grant(TimeSpan.FromHours(8));
        var revokedAt = Now.AddMinutes(10);

        request.TryRevoke(Guid.CreateVersion7(), "admin@company.local", null, revokedAt).ShouldBeTrue();

        request.Status.ShouldBe(UsbAccessRequestStatus.Revoked);
        request.ExpiresAt.ShouldBe(revokedAt);
        request.IsLive(revokedAt).ShouldBeFalse();
        request.IsLive(Now.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void Revoking_twice_is_refused_rather_than_silently_repeated()
    {
        var request = Grant(TimeSpan.FromHours(1));
        var actor = Guid.CreateVersion7();

        request.TryRevoke(actor, "admin", null, Now.AddMinutes(1)).ShouldBeTrue();
        request.TryRevoke(actor, "admin", null, Now.AddMinutes(2)).ShouldBeFalse();
    }

    [Fact]
    public void Expiry_only_applies_once_the_deadline_has_actually_passed()
    {
        var request = Grant(TimeSpan.FromHours(1));

        request.TryExpire(Now.AddMinutes(59)).ShouldBeFalse();
        request.Status.ShouldBe(UsbAccessRequestStatus.Approved);

        request.TryExpire(Now.AddHours(1)).ShouldBeTrue();
        request.Status.ShouldBe(UsbAccessRequestStatus.Expired);

        // Terminal: a second sweep does not re-expire it.
        request.TryExpire(Now.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void A_revoked_grant_is_not_re_expired_by_the_sweeper()
    {
        var request = Grant(TimeSpan.FromHours(1));
        request.TryRevoke(Guid.CreateVersion7(), "admin", null, Now.AddMinutes(5));

        request.TryExpire(Now.AddHours(2)).ShouldBeFalse();
        request.Status.ShouldBe(UsbAccessRequestStatus.Revoked);
    }

    [Fact]
    public void A_grant_requires_a_justification()
    {
        Should.Throw<ArgumentException>(() => UsbAccessRequest.GrantByAdministrator(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            @"USB\VID_0781&PID_5581\ABC123",
            justification: "   ",
            Guid.CreateVersion7(), "admin", TimeSpan.FromHours(1), Now));
    }
}
