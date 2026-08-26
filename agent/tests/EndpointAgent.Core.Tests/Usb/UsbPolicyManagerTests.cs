using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// The agent's half of the USB security model.
/// </summary>
/// <remarks>
/// The server can decide whatever it likes; these tests are about what the
/// endpoint actually does with that decision — and, more importantly, what it
/// does when the decision never arrives, arrives late, arrives corrupted, or has
/// expired. Every one of those paths must end with the device restricted.
/// </remarks>
public sealed class UsbPolicyManagerTests
{
    private const string StickId = @"USB\VID_0781&PID_5581\ABC123";
    private const string OtherStickId = @"USB\VID_0930&PID_6544\XYZ789";
    private const string KeyboardId = @"USB\VID_046D&PID_C31C\5&12345&0&1";

    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static UsbDeviceInfo Storage(string instanceId = StickId) =>
        new(instanceId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer", null, IsEnabled: true);

    private static UsbDeviceInfo Keyboard() =>
        new(KeyboardId, UsbClass.Keyboard, "046D", "C31C", null, "Logitech", "Keyboard", null, IsEnabled: true);

    private static (UsbPolicyManager Manager, FakeEnforcer Enforcer, FakeGrantStore Store, TestClock Clock)
        Build(params UsbDeviceInfo[] devices)
    {
        var enumerator = new FakeEnumerator(devices);
        var enforcer = new FakeEnforcer();
        var store = new FakeGrantStore();
        var clock = new TestClock(Start);

        var manager = new UsbPolicyManager(
            enumerator, enforcer, store, new FakeLedger(), clock, NullLogger<UsbPolicyManager>.Instance);

        return (manager, enforcer, store, clock);
    }

    // ---- the default -------------------------------------------------------

    [Fact]
    public async Task Storage_with_no_policy_at_all_is_restricted()
    {
        var (manager, enforcer, _, _) = Build(Storage());

        var outcome = await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
        outcome.Restricted.ShouldBe(1);
        outcome.ReadOnly.ShouldBe(0);
    }

    /// <summary>
    /// A machine that has never reached the server still restricts.
    /// </summary>
    /// <remarks>
    /// This is the case that decides whether the control is real. If the endpoint
    /// waited for a policy before acting, then unplugging the network cable would
    /// be the bypass.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_grant_store_restricts_everything()
    {
        var enumerator = new FakeEnumerator([Storage()]);
        var enforcer = new FakeEnforcer();
        var store = new FakeGrantStore { ThrowOnLoad = true };

        var manager = new UsbPolicyManager(
            enumerator, enforcer, store, new FakeLedger(), new TestClock(Start),
            NullLogger<UsbPolicyManager>.Instance);

        await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task Non_storage_devices_are_never_touched()
    {
        var (manager, enforcer, _, _) = Build(Keyboard());

        var outcome = await manager.ReconcileAsync();

        enforcer.Calls.ShouldBeEmpty();
        outcome.Total.ShouldBe(0);
    }

    // ---- grants ------------------------------------------------------------

    [Fact]
    public async Task A_live_grant_makes_exactly_that_device_read_only()
    {
        var (manager, enforcer, _, _) = Build(Storage(), Storage(OtherStickId));

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId), ("Restrict", OtherStickId)], ignoreOrder: true);
    }

    /// <summary>
    /// Expiry is enforced by the endpoint's own clock, with no server involved.
    /// </summary>
    [Fact]
    public async Task A_grant_lapses_on_time_with_no_further_contact()
    {
        var (manager, enforcer, _, clock) = Build(Storage());

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(1))], Start);
        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);

        enforcer.Calls.Clear();
        clock.Advance(TimeSpan.FromMinutes(59));
        await manager.ReconcileAsync();
        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);

        // Deadline passes. Nothing has been received from the server since.
        enforcer.Calls.Clear();
        clock.Advance(TimeSpan.FromMinutes(2));
        await manager.ReconcileAsync();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task An_already_expired_grant_is_never_applied()
    {
        var (manager, enforcer, _, _) = Build(Storage());

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(-1))], Start);

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task An_empty_policy_revokes_everything()
    {
        var (manager, enforcer, _, _) = Build(Storage());

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);
        enforcer.Calls.Clear();

        await manager.ApplyPolicyAsync([], Start.AddMinutes(1));

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    /// <summary>
    /// A task queued before a revocation must not undo it by arriving after.
    /// </summary>
    /// <remarks>
    /// Entirely reachable in practice: grant a device, revoke it a minute later,
    /// and a laptop that was asleep for both receives the two policy tasks in
    /// whatever order it drains its queue. Without the issued-at check the older
    /// one could land second and reinstate the access.
    /// </remarks>
    [Fact]
    public async Task A_stale_policy_arriving_late_cannot_reinstate_revoked_access()
    {
        var (manager, enforcer, _, _) = Build(Storage());

        // The revocation, issued second, arrives first.
        await manager.ApplyPolicyAsync([], Start.AddMinutes(5));
        enforcer.Calls.Clear();

        // The older grant turns up afterwards.
        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task A_grant_for_a_different_device_does_not_leak_across()
    {
        var (manager, enforcer, _, _) = Build(Storage(OtherStickId));

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        enforcer.Calls.ShouldBe([("Restrict", OtherStickId)]);
    }

    // ---- persistence -------------------------------------------------------

    [Fact]
    public async Task An_applied_policy_is_persisted_so_a_restart_keeps_honouring_it()
    {
        var (manager, _, store, _) = Build(Storage());

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        store.Saved.ShouldNotBeNull();
        store.Saved!.Grants.Single().InstanceId.ShouldBe(StickId);

        // A fresh manager over the same store — the reboot case.
        var enforcer = new FakeEnforcer();
        var restarted = new UsbPolicyManager(
            new FakeEnumerator([Storage()]), enforcer, store, new FakeLedger(),
            new TestClock(Start.AddMinutes(1)), NullLogger<UsbPolicyManager>.Instance);

        await restarted.ReconcileAsync();

        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);
    }

    // ---- honesty about enforcement ----------------------------------------

    [Fact]
    public async Task A_failed_enforcement_is_reported_rather_than_swallowed()
    {
        var enumerator = new FakeEnumerator([Storage()]);
        var enforcer = new FakeEnforcer { FailWith = "access denied" };

        var manager = new UsbPolicyManager(
            enumerator, enforcer, new FakeGrantStore(), new FakeLedger(),
            new TestClock(Start), NullLogger<UsbPolicyManager>.Instance);

        var outcome = await manager.ReconcileAsync();
        outcome.Failed.ShouldBe(1);

        var report = manager.BuildReport();
        var entry = report.Devices.Single();

        entry.EnforcedPolicy.ShouldBeNull();
        entry.EnforcementError.ShouldBe("access denied");
    }

    [Fact]
    public async Task An_enforcer_that_throws_does_not_take_the_agent_down()
    {
        var enumerator = new FakeEnumerator([Storage()]);
        var enforcer = new FakeEnforcer { ThrowWith = "the driver exploded" };

        var manager = new UsbPolicyManager(
            enumerator, enforcer, new FakeGrantStore(), new FakeLedger(),
            new TestClock(Start), NullLogger<UsbPolicyManager>.Instance);

        var outcome = await manager.ReconcileAsync();

        outcome.Failed.ShouldBe(1);
        manager.BuildReport().Devices.Single().EnforcementError.ShouldBe("the driver exploded");
    }

    [Fact]
    public async Task The_report_states_what_is_enforced_not_what_was_asked_for()
    {
        var (manager, _, _, _) = Build(Storage(), Keyboard());

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        var report = manager.BuildReport();

        report.Devices.Single(d => d.InstanceId == StickId).EnforcedPolicy.ShouldBe("ReadOnly");

        // A keyboard has no storage policy, and the report says so rather than
        // implying it is restricted.
        var keyboard = report.Devices.Single(d => d.InstanceId == KeyboardId);
        keyboard.EnforcedPolicy.ShouldBeNull();
        keyboard.DeviceClass.ShouldBe("Keyboard");
    }

    [Fact]
    public async Task Enumeration_failing_does_not_throw_or_grant_anything()
    {
        var enforcer = new FakeEnforcer();
        var manager = new UsbPolicyManager(
            new FakeEnumerator([]) { Throw = true }, enforcer, new FakeGrantStore(), new FakeLedger(),
            new TestClock(Start), NullLogger<UsbPolicyManager>.Instance);

        var outcome = await manager.ReconcileAsync();

        outcome.Total.ShouldBe(0);
        enforcer.Calls.ShouldBeEmpty();
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeEnumerator(UsbDeviceInfo[] devices) : IUsbDeviceEnumerator
    {
        public bool Throw { get; init; }

        public IReadOnlyList<UsbDeviceInfo> Enumerate() =>
            Throw ? throw new InvalidOperationException("enumeration failed") : devices;
    }

    private sealed class FakeEnforcer : IUsbPolicyEnforcer
    {
        public List<(string Action, string InstanceId)> Calls { get; } = [];

        public string? FailWith { get; init; }

        public string? ThrowWith { get; init; }

        public UsbEnforcementResult Restrict(string instanceId) => Record("Restrict", instanceId);

        public UsbEnforcementResult AllowReadOnly(string instanceId) => Record("AllowReadOnly", instanceId);

        public UsbEnforcementResult AllowReadWrite(string instanceId) =>
            Record("AllowReadWrite", instanceId);

        public UsbEnforcementResult Release(string instanceId) => Record("Release", instanceId);

        /// <summary>Instance IDs that <see cref="Release"/> alone should fail for.</summary>
        public HashSet<string> FailReleaseFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        private UsbEnforcementResult Record(string action, string instanceId)
        {
            Calls.Add((action, instanceId));

            if (action == "Release" && FailReleaseFor.Contains(instanceId))
            {
                return UsbEnforcementResult.Failed("the device would not re-enable");
            }


            if (ThrowWith is not null)
            {
                throw new InvalidOperationException(ThrowWith);
            }

            return FailWith is null ? UsbEnforcementResult.Ok : UsbEnforcementResult.Failed(FailWith);
        }
    }

    private sealed class FakeLedger : IUsbRestrictionLedger
    {
        public IReadOnlyCollection<string> Saved { get; private set; } = [];

        public bool ThrowOnLoad { get; init; }

        public ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            ThrowOnLoad
                ? throw new IOException("the release list is unreadable")
                : ValueTask.FromResult(Saved);

        public ValueTask SaveAsync(
            IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default)
        {
            Saved = instanceIds;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGrantStore : IUsbGrantStore
    {
        public UsbGrantSet? Saved { get; private set; }

        public bool ThrowOnLoad { get; init; }

        public ValueTask<UsbGrantSet> LoadAsync(CancellationToken cancellationToken = default) =>
            ThrowOnLoad
                ? throw new IOException("the grant file is unreadable")
                : ValueTask.FromResult(Saved ?? UsbGrantSet.Empty);

        public ValueTask SaveAsync(UsbGrantSet grants, CancellationToken cancellationToken = default)
        {
            Saved = grants;
            return ValueTask.CompletedTask;
        }
    }
}
