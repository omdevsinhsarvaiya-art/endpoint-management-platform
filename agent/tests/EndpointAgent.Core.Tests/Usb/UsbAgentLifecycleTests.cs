using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// What USB control does across the agent's own lifecycle: running, stopped,
/// restarted, uninstalled.
/// </summary>
/// <remarks>
/// <para>
/// These pin a property the rest of the USB tests deliberately do not cover:
/// <b>this product's control over USB lasts exactly as long as the agent is
/// running, and not one moment longer.</b> A stopped agent must leave an ordinary
/// Windows machine behind.
/// </para>
/// <para>
/// The distinction being tested is between two kinds of state that are easy to
/// conflate. The <em>policy</em> — which devices an administrator has approved —
/// is durable and survives a stop, which is what lets a restart re-establish
/// enforcement without contacting the server. The <em>enforcement</em> — the
/// disabled devnode, the read-only disk attribute — is not durable and is
/// explicitly undone on the way out. Getting this backwards is what the original
/// implementation did: enforcement outlived the agent because disabling a devnode
/// writes a registry flag Windows honours forever, so stopping the service left
/// the machine restricted with nothing running that could lift it.
/// </para>
/// </remarks>
public sealed class UsbAgentLifecycleTests
{
    private const string StickId = @"USB\VID_0781&PID_5581\ABC123";
    private const string OtherStickId = @"USB\VID_0930&PID_6544\XYZ789";

    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static UsbDeviceInfo Storage(string instanceId = StickId) =>
        new(instanceId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer", null, IsEnabled: true);

    /// <summary>
    /// One machine across several agent lifetimes.
    /// </summary>
    /// <remarks>
    /// The stores are the machine's disk and outlive any single manager; a new
    /// manager over the same stores is a service restart. The enforcer is the
    /// machine's device state, so its call log spans the whole scenario.
    /// </remarks>
    private sealed class Machine
    {
        public FakeEnforcer Enforcer { get; } = new();

        public FakeGrantStore Grants { get; } = new();

        public FakeLedger Ledger { get; } = new();

        public TestClock Clock { get; } = new(Start);

        public UsbDeviceInfo[] Attached { get; set; } = [Storage()];

        /// <summary>Starts an agent against this machine's persisted state.</summary>
        public UsbPolicyManager StartAgent() =>
            new(new FakeEnumerator(Attached), Enforcer, Grants, Ledger, Clock,
                NullLogger<UsbPolicyManager>.Instance);
    }

    // ---- 1. agent running --------------------------------------------------

    [Fact]
    public async Task While_the_agent_runs_storage_is_restricted_by_default_and_read_only_only_when_granted()
    {
        var machine = new Machine { Attached = [Storage(), Storage(OtherStickId)] };
        var agent = machine.StartAgent();

        await agent.ReconcileAsync();
        machine.Enforcer.Calls.ShouldBe(
            [("Restrict", StickId), ("Restrict", OtherStickId)], ignoreOrder: true);

        machine.Enforcer.Calls.Clear();
        await agent.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        machine.Enforcer.Calls.ShouldBe(
            [("AllowReadOnly", StickId), ("Restrict", OtherStickId)], ignoreOrder: true);
    }

    /// <summary>
    /// Revoking returns the device to Restricted, not to normal.
    /// </summary>
    /// <remarks>
    /// Worth separating from release explicitly. Revocation is the administrator
    /// narrowing access on a machine that is still managed, so the device goes
    /// back to the restricted default. Release is the product standing down, and
    /// only that returns the device to unmanaged behaviour. Collapsing the two
    /// would mean revoking a grant handed the user a writable stick.
    /// </remarks>
    [Fact]
    public async Task Revoking_a_grant_returns_the_device_to_restricted_not_to_normal()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();

        await agent.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);
        machine.Enforcer.Calls.Clear();

        await agent.ApplyPolicyAsync([], Start.AddMinutes(1));

        machine.Enforcer.Calls.ShouldBe([("Restrict", StickId)]);
        machine.Enforcer.Calls.ShouldNotContain(c => c.Action == "Release");
    }

    // ---- 2. agent stopped --------------------------------------------------

    [Fact]
    public async Task Stopping_the_agent_releases_every_device_it_was_enforcing()
    {
        var machine = new Machine { Attached = [Storage(), Storage(OtherStickId)] };
        var agent = machine.StartAgent();

        await agent.ReconcileAsync();
        machine.Enforcer.Calls.Clear();

        var outcome = await agent.ReleaseAllAsync();

        outcome.Released.ShouldBe(2);
        outcome.Failed.ShouldBe(0);
        machine.Enforcer.Calls.ShouldBe(
            [("Release", StickId), ("Release", OtherStickId)], ignoreOrder: true);
    }

    /// <summary>
    /// A stopped agent does not keep the administrator's decision from being
    /// changed later.
    /// </summary>
    /// <remarks>
    /// The policy has to survive the stop — otherwise a reboot would silently
    /// drop every restriction and the device would come back unmanaged until the
    /// server was reachable. So: enforcement released, policy retained.
    /// </remarks>
    [Fact]
    public async Task Releasing_on_stop_keeps_the_policy_for_the_next_start()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();

        await agent.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(4))], Start);
        var savedBefore = machine.Grants.Saved;

        await agent.ReleaseAllAsync();

        machine.Grants.Saved.ShouldBe(savedBefore);
        machine.Grants.Saved!.Grants.Single().InstanceId.ShouldBe(StickId);
    }

    [Fact]
    public async Task A_device_attached_while_the_agent_is_stopped_is_not_touched()
    {
        var machine = new Machine();
        var agent = machine.StartAgent();

        await agent.ReconcileAsync();
        await agent.ReleaseAllAsync();
        machine.Enforcer.Calls.Clear();

        // The agent is stopped. A second stick is plugged in; nothing is running
        // to enumerate or enforce it, so nothing happens to it.
        machine.Attached = [Storage(), Storage(OtherStickId)];

        machine.Enforcer.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// Release does not re-disable a device when it fails.
    /// </summary>
    /// <remarks>
    /// The inverse of the rule everywhere else in this feature. Enforcement fails
    /// closed, towards Restricted; release fails <em>open</em>, because a release
    /// that fell back to restricting would defeat its own purpose. The device
    /// stays on the list so the next attempt can finish the job.
    /// </remarks>
    [Fact]
    public async Task A_device_that_fails_to_release_is_kept_for_the_next_attempt()
    {
        var machine = new Machine { Attached = [Storage(), Storage(OtherStickId)] };
        machine.Enforcer.FailReleaseFor.Add(OtherStickId);

        var agent = machine.StartAgent();
        await agent.ReconcileAsync();
        machine.Enforcer.Calls.Clear();

        var outcome = await agent.ReleaseAllAsync();

        outcome.Released.ShouldBe(1);
        outcome.Failed.ShouldBe(1);

        // Not re-restricted on the way out.
        machine.Enforcer.Calls.ShouldNotContain(c => c.Action == "Restrict");

        // Still on the list, so an uninstall or the next stop retries it.
        machine.Ledger.Saved.ShouldBe([OtherStickId]);
    }

    // ---- 3. agent restarted ------------------------------------------------

    /// <summary>
    /// The full cycle the clarification asks for: enforced, released, enforced
    /// again — with the restriction restored from local state alone.
    /// </summary>
    [Fact]
    public async Task A_restart_restores_enforcement_from_local_state_with_no_server_contact()
    {
        var machine = new Machine();

        // First lifetime: restrict, then stop cleanly.
        var first = machine.StartAgent();
        await first.ReconcileAsync();
        await first.ReleaseAllAsync();

        machine.Enforcer.Calls.ShouldBe([("Restrict", StickId), ("Release", StickId)]);
        machine.Enforcer.Calls.Clear();

        // Second lifetime: nothing is passed between them but the two stores.
        var second = machine.StartAgent();
        await second.ReconcileAsync();

        machine.Enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task A_restart_restores_a_read_only_grant_that_has_not_yet_expired()
    {
        var machine = new Machine();

        var first = machine.StartAgent();
        await first.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(3))], Start);
        await first.ReleaseAllAsync();
        machine.Enforcer.Calls.Clear();

        // An hour of downtime, well inside the grant.
        machine.Clock.Advance(TimeSpan.FromHours(1));

        var second = machine.StartAgent();
        await second.ReconcileAsync();

        machine.Enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);
    }

    /// <summary>
    /// Downtime does not extend a grant.
    /// </summary>
    /// <remarks>
    /// Stopping the agent over the deadline must not be a way to come back with
    /// access still live. The deadline is absolute, so it lapses whether or not
    /// anything was running to notice.
    /// </remarks>
    [Fact]
    public async Task A_grant_that_expired_while_the_agent_was_stopped_is_not_restored()
    {
        var machine = new Machine();

        var first = machine.StartAgent();
        await first.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddMinutes(30))], Start);
        await first.ReleaseAllAsync();
        machine.Enforcer.Calls.Clear();

        machine.Clock.Advance(TimeSpan.FromHours(2));

        var second = machine.StartAgent();
        await second.ReconcileAsync();

        machine.Enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    /// <summary>
    /// The hard-kill case: no release ran, so the restart has to clean up.
    /// </summary>
    /// <remarks>
    /// This is the gap the shutdown hook cannot close — a killed process, a
    /// bugcheck, a power cut. The ledger written during enforcement is what makes
    /// it recoverable: the next start finds the device on the list and reconciles
    /// it, rather than leaving state behind that nothing remembers applying.
    /// </remarks>
    [Fact]
    public async Task After_an_unclean_stop_the_next_start_still_knows_what_it_had_restricted()
    {
        var machine = new Machine();

        var killed = machine.StartAgent();
        await killed.ReconcileAsync();

        // No ReleaseAllAsync: the process died.
        machine.Ledger.Saved.ShouldBe([StickId]);

        var restarted = machine.StartAgent();
        var outcome = await restarted.ReleaseAllAsync();

        outcome.Released.ShouldBe(1);
    }

    // ---- 4. agent uninstalled ----------------------------------------------

    /// <summary>
    /// Uninstall must release a device even when it is not plugged in.
    /// </summary>
    /// <remarks>
    /// The case that makes the ledger necessary rather than merely tidy. A
    /// disabled devnode keeps its registry flag while unplugged, so enumeration
    /// cannot find the device to release it — and the damage would only appear
    /// later, when somebody plugged the stick into a machine that no longer has
    /// the product installed and found it dead.
    /// </remarks>
    [Fact]
    public async Task Uninstall_releases_a_device_that_is_no_longer_attached()
    {
        var machine = new Machine();

        var agent = machine.StartAgent();
        await agent.ReconcileAsync();
        machine.Enforcer.Calls.Clear();

        // The stick is pulled out before the product is removed.
        machine.Attached = [];

        var uninstaller = machine.StartAgent();
        var outcome = await uninstaller.ReleaseAllAsync();

        outcome.Released.ShouldBe(1);
        machine.Enforcer.Calls.ShouldBe([("Release", StickId)]);
    }

    [Fact]
    public async Task Once_everything_is_released_the_list_is_empty_and_release_is_a_no_op()
    {
        var machine = new Machine();

        var agent = machine.StartAgent();
        await agent.ReconcileAsync();
        await agent.ReleaseAllAsync();

        machine.Ledger.Saved.ShouldBeEmpty();

        machine.Enforcer.Calls.Clear();
        var again = await agent.ReleaseAllAsync();

        again.Released.ShouldBe(0);
        machine.Enforcer.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// Devices an administrator disabled by hand are left alone.
    /// </summary>
    /// <remarks>
    /// Release undoes this product's work, not the machine owner's. Enabling a
    /// device we never disabled would be this feature reaching past its own remit
    /// on the way out of the door.
    /// </remarks>
    [Fact]
    public async Task Release_only_touches_devices_this_agent_applied_state_to()
    {
        var machine = new Machine();

        var agent = machine.StartAgent();
        await agent.ReconcileAsync();

        // A second stick appears that this agent has never enforced — it was
        // disabled in Device Manager by the machine's owner.
        machine.Attached = [Storage(), Storage(OtherStickId)];
        machine.Enforcer.Calls.Clear();

        var uninstaller = new UsbPolicyManager(
            new FakeEnumerator([]), machine.Enforcer, machine.Grants, machine.Ledger, machine.Clock,
            NullLogger<UsbPolicyManager>.Instance);

        await uninstaller.ReleaseAllAsync();

        machine.Enforcer.Calls.ShouldBe([("Release", StickId)]);
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeEnumerator(UsbDeviceInfo[] devices) : IUsbDeviceEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate() => devices;
    }

    private sealed class FakeEnforcer : IUsbPolicyEnforcer
    {
        public List<(string Action, string InstanceId)> Calls { get; } = [];

        public HashSet<string> FailReleaseFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        public UsbEnforcementResult Restrict(string instanceId) => Record("Restrict", instanceId);

        public UsbEnforcementResult AllowReadOnly(string instanceId) => Record("AllowReadOnly", instanceId);

        public UsbEnforcementResult AllowReadWrite(string instanceId) =>
            Record("AllowReadWrite", instanceId);

        public UsbEnforcementResult Release(string instanceId) => Record("Release", instanceId);

        private UsbEnforcementResult Record(string action, string instanceId)
        {
            Calls.Add((action, instanceId));

            return action == "Release" && FailReleaseFor.Contains(instanceId)
                ? UsbEnforcementResult.Failed("the device would not re-enable")
                : UsbEnforcementResult.Ok;
        }
    }

    private sealed class FakeGrantStore : IUsbGrantStore
    {
        public UsbGrantSet? Saved { get; private set; }

        public ValueTask<UsbGrantSet> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Saved ?? UsbGrantSet.Empty);

        public ValueTask SaveAsync(UsbGrantSet grants, CancellationToken cancellationToken = default)
        {
            Saved = grants;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLedger : IUsbRestrictionLedger
    {
        public IReadOnlyCollection<string> Saved { get; private set; } = [];

        public ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Saved);

        public ValueTask SaveAsync(
            IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default)
        {
            Saved = instanceIds;
            return ValueTask.CompletedTask;
        }
    }
}
