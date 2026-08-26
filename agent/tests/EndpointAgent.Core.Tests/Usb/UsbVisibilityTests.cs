using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// A device the agent has restricted must stay visible to the console.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against was circular. Restricting a device disables
/// its devnode, which unloads the driver and removes the child devnodes — the
/// two signals the enumerator used to recognise storage. The device then looked
/// like an anonymous peripheral, dropped out of the console's storage view, and
/// could never be granted access again. The control destroyed the evidence that
/// it applied to anything.
/// </para>
/// <para>
/// Recognition now also comes from the device's own compatible IDs, which
/// survive being disabled. These tests pin the consequence at the reporting
/// layer: a restricted stick is still reported, still classed Storage, and still
/// carries the state being enforced on it.
/// </para>
/// </remarks>
public sealed class UsbVisibilityTests
{
    private const string StickId = @"USB\VID_0781&PID_5581\ABC123";

    private static readonly DateTimeOffset Start = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A stick as it looks once the agent has disabled it.</summary>
    private static UsbDeviceInfo DisabledStick() =>
        new(StickId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer",
            @"USB\VID_0781&PID_5581", IsEnabled: false);

    private static UsbDeviceInfo EnabledStick() =>
        new(StickId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer",
            @"USB\VID_0781&PID_5581", IsEnabled: true);

    private static (UsbPolicyManager Manager, Recording Enforcer) Build(params UsbDeviceInfo[] devices)
    {
        var enforcer = new Recording();
        var manager = new UsbPolicyManager(
            new Fixed(devices), enforcer, new Store(), new Ledger(),
            new TestClock(Start), NullLogger<UsbPolicyManager>.Instance);

        return (manager, enforcer);
    }

    [Fact]
    public async Task A_restricted_stick_is_still_reported_as_attached_storage()
    {
        var (manager, _) = Build(DisabledStick());

        await manager.ReconcileAsync();
        var entry = manager.BuildReport().Devices.Single();

        entry.InstanceId.ShouldBe(StickId);
        entry.DeviceClass.ShouldBe("Storage");
        entry.IsConnected.ShouldBeTrue();
        entry.EnforcedPolicy.ShouldBe("Restricted");
        entry.EnforcementError.ShouldBeNull();
    }

    /// <summary>
    /// Being disabled does not stop the agent enforcing on it.
    /// </summary>
    /// <remarks>
    /// Enforcement is idempotent, so re-restricting an already-restricted device
    /// is a no-op — but it has to keep happening, because that is what detects a
    /// local administrator re-enabling the device by hand.
    /// </remarks>
    [Fact]
    public async Task A_disabled_stick_is_still_reconciled_rather_than_skipped()
    {
        var (manager, enforcer) = Build(DisabledStick());

        var outcome = await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
        outcome.Restricted.ShouldBe(1);
    }

    [Theory]
    [InlineData(UsbEnforcedState.ReadOnly, "ReadOnly", "AllowReadOnly")]
    [InlineData(UsbEnforcedState.Enabled, "Enabled", "AllowReadWrite")]
    public async Task A_granted_stick_reports_the_level_in_force(
        UsbEnforcedState level, string expectedReport, string expectedCall)
    {
        var (manager, enforcer) = Build(EnabledStick());

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2), level)], Start);

        enforcer.Calls.ShouldBe([(expectedCall, StickId)]);
        manager.BuildReport().Devices.Single().EnforcedPolicy.ShouldBe(expectedReport);
    }

    /// <summary>
    /// A failure is reported as a failure, not as the state that was wanted.
    /// </summary>
    /// <remarks>
    /// This is what lets the console show "Enforcement failed" instead of a green
    /// tick beside a control that is not actually in place.
    /// </remarks>
    [Fact]
    public async Task A_device_that_could_not_be_enforced_reports_the_error_not_the_policy()
    {
        var enforcer = new Recording { FailWith = "access denied" };
        var manager = new UsbPolicyManager(
            new Fixed([DisabledStick()]), enforcer, new Store(), new Ledger(),
            new TestClock(Start), NullLogger<UsbPolicyManager>.Instance);

        await manager.ReconcileAsync();
        var entry = manager.BuildReport().Devices.Single();

        entry.EnforcedPolicy.ShouldBeNull();
        entry.EnforcementError.ShouldBe("access denied");
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class Fixed(UsbDeviceInfo[] devices) : IUsbDeviceEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate() => devices;
    }

    private sealed class Recording : IUsbPolicyEnforcer
    {
        public List<(string Action, string InstanceId)> Calls { get; } = [];

        public string? FailWith { get; init; }

        public UsbEnforcementResult Restrict(string instanceId) => Record("Restrict", instanceId);

        public UsbEnforcementResult AllowReadOnly(string instanceId) => Record("AllowReadOnly", instanceId);

        public UsbEnforcementResult AllowReadWrite(string instanceId) => Record("AllowReadWrite", instanceId);

        public UsbEnforcementResult Release(string instanceId) => Record("Release", instanceId);

        private UsbEnforcementResult Record(string action, string instanceId)
        {
            Calls.Add((action, instanceId));
            return FailWith is null ? UsbEnforcementResult.Ok : UsbEnforcementResult.Failed(FailWith);
        }
    }

    private sealed class Store : IUsbGrantStore
    {
        private UsbGrantSet _grants = UsbGrantSet.Empty;

        public ValueTask<UsbGrantSet> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_grants);

        public ValueTask SaveAsync(UsbGrantSet grants, CancellationToken cancellationToken = default)
        {
            _grants = grants;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Ledger : IUsbRestrictionLedger
    {
        private IReadOnlyCollection<string> _ids = [];

        public ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_ids);

        public ValueTask SaveAsync(
            IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default)
        {
            _ids = instanceIds;
            return ValueTask.CompletedTask;
        }
    }
}
