using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointAgent.Core.Usb;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// The three access levels, and the rule that only two of them can be granted.
/// </summary>
/// <remarks>
/// <para>
/// Read/write access is the widest thing this platform can do to an endpoint, so
/// the tests that matter most here are the negative ones: every way a payload
/// might try to reach <see cref="UsbEnforcedState.Enabled"/> without an
/// administrator having named it. An unknown policy string, the enum's ordinal
/// as a number, a bare <c>2</c>, an expired grant, a grant for a different
/// device — each must land on Restricted rather than on write access.
/// </para>
/// <para>
/// Restricted is deliberately not grantable. It is the absence of a grant, and
/// an entry claiming to confer it is malformed rather than meaningful; treating
/// it as a grant would attach an expiry to a state that does not have one.
/// </para>
/// </remarks>
public sealed class UsbAccessLevelTests
{
    private const string StickId = @"USB\VID_0781&PID_5581\ABC123";
    private const string OtherStickId = @"USB\VID_0930&PID_6544\XYZ789";

    private static readonly DateTimeOffset Start = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static UsbDeviceInfo Storage(string instanceId = StickId) =>
        new(instanceId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer", null, IsEnabled: true);

    private static (UsbPolicyManager Manager, RecordingEnforcer Enforcer, TestClock Clock)
        Build(params UsbDeviceInfo[] devices)
    {
        var enforcer = new RecordingEnforcer();
        var clock = new TestClock(Start);

        var manager = new UsbPolicyManager(
            new FakeEnumerator(devices.Length == 0 ? [Storage()] : devices),
            enforcer, new FakeGrantStore(), new FakeLedger(), clock,
            NullLogger<UsbPolicyManager>.Instance);

        return (manager, enforcer, clock);
    }

    private static (ApplyUsbPolicyExecutor Executor, RecordingEnforcer Enforcer) BuildExecutor()
    {
        var (manager, enforcer, clock) = BuildWithClock();
        return (new ApplyUsbPolicyExecutor(manager, clock, NullLogger<ApplyUsbPolicyExecutor>.Instance), enforcer);
    }

    private static (UsbPolicyManager, RecordingEnforcer, TestClock) BuildWithClock() => Build();

    private static AgentTask Task(string payload) => new(Guid.CreateVersion7(), "ApplyUsbPolicy", payload);

    private static string Payload(string policyJson) => $$"""
        {"grants":[{"instanceId":"USB\\VID_0781&PID_5581\\ABC123",
         "policy":{{policyJson}},"expiresAt":"2026-08-26T14:00:00+00:00"}],
         "issuedAt":"2026-08-26T12:00:00+00:00"}
        """;

    // ---- the three levels --------------------------------------------------

    [Fact]
    public async Task No_grant_means_restricted()
    {
        var (manager, enforcer, _) = Build();

        await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task A_read_only_grant_enforces_read_only()
    {
        var (manager, enforcer, _) = Build();

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.ReadOnly)], Start);

        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);
    }

    [Fact]
    public async Task An_enabled_grant_enforces_read_write()
    {
        var (manager, enforcer, _) = Build();

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.Enabled)], Start);

        enforcer.Calls.ShouldBe([("AllowReadWrite", StickId)]);
    }

    /// <summary>
    /// The report says which of the three levels is in force, by name.
    /// </summary>
    [Fact]
    public async Task The_report_names_the_level_actually_in_force()
    {
        var (manager, _, _) = Build(Storage(), Storage(OtherStickId));

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.Enabled)], Start);

        var report = manager.BuildReport();

        report.Devices.Single(d => d.InstanceId == StickId).EnforcedPolicy.ShouldBe("Enabled");
        report.Devices.Single(d => d.InstanceId == OtherStickId).EnforcedPolicy.ShouldBe("Restricted");
    }

    // ---- revocation returns to Restricted, not to normal -------------------

    [Fact]
    public async Task Revoking_a_read_write_grant_returns_the_device_to_restricted()
    {
        var (manager, enforcer, _) = Build();

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.Enabled)], Start);
        enforcer.Calls.Clear();

        await manager.ApplyPolicyAsync([], Start.AddMinutes(1));

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task A_read_write_grant_lapses_on_time_with_no_server_contact()
    {
        var (manager, enforcer, clock) = Build();

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(1), UsbEnforcedState.Enabled)], Start);
        enforcer.Calls.Clear();

        clock.Advance(TimeSpan.FromMinutes(61));
        await manager.ReconcileAsync();

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    /// <summary>
    /// Restricted cannot be granted; an entry claiming to is ignored.
    /// </summary>
    [Fact]
    public async Task A_grant_naming_restricted_is_ignored_rather_than_honoured()
    {
        var (manager, enforcer, _) = Build();

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.Restricted)], Start);

        // Restricted either way — but via "no live grant", not via the entry.
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    // ---- the wire: only two exact names reach write access -----------------

    [Fact]
    public async Task The_wire_name_Enabled_reaches_read_write()
    {
        var (executor, enforcer) = BuildExecutor();

        var result = await executor.ExecuteAsync(Task(Payload("\"Enabled\"")));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("AllowReadWrite", StickId)]);
    }

    /// <summary>
    /// Every other spelling lands on Restricted.
    /// </summary>
    /// <remarks>
    /// <c>2</c> and <c>"2"</c> are the ones worth stating: <c>Enum.TryParse</c>
    /// would accept both as <see cref="UsbEnforcedState.Enabled"/>, so a payload
    /// carrying a bare ordinal could obtain write access without ever naming it.
    /// The parser refuses anything that is not one of the two exact strings.
    /// </remarks>
    [Theory]
    [InlineData("2")]
    [InlineData("\"2\"")]
    [InlineData("\"enable\"")]
    [InlineData("\"ReadWrite\"")]
    [InlineData("\"FullAccess\"")]
    [InlineData("\"Writable\"")]
    [InlineData("\"Restricted\"")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task No_other_spelling_can_reach_read_write(string policyJson)
    {
        var (executor, enforcer) = BuildExecutor();

        var result = await executor.ExecuteAsync(Task(Payload(policyJson)));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task An_expired_read_write_grant_is_dropped_at_parse_time()
    {
        var (executor, enforcer) = BuildExecutor();

        var result = await executor.ExecuteAsync(Task("""
            {"grants":[{"instanceId":"USB\\VID_0781&PID_5581\\ABC123",
             "policy":"Enabled","expiresAt":"2026-08-26T11:00:00+00:00"}],
             "issuedAt":"2026-08-26T12:00:00+00:00"}
            """));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    /// <summary>
    /// A stale read/write policy cannot reinstate access that was revoked.
    /// </summary>
    [Fact]
    public async Task A_late_read_write_policy_cannot_undo_a_revocation()
    {
        var (manager, enforcer, _) = Build();

        await manager.ApplyPolicyAsync([], Start.AddMinutes(5));
        enforcer.Calls.Clear();

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.Enabled)], Start);

        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task A_read_write_grant_does_not_leak_to_another_device()
    {
        var (manager, enforcer, _) = Build(Storage(), Storage(OtherStickId));

        await manager.ApplyPolicyAsync(
            [new UsbGrantRecord(StickId, Start.AddHours(2), UsbEnforcedState.Enabled)], Start);

        enforcer.Calls.ShouldBe(
            [("AllowReadWrite", StickId), ("Restrict", OtherStickId)], ignoreOrder: true);
    }

    /// <summary>
    /// A cache written by an older agent has no policy field; it reads as
    /// read-only, never as write access.
    /// </summary>
    [Fact]
    public async Task A_grant_with_no_stated_level_defaults_to_read_only()
    {
        var (manager, enforcer, _) = Build();

        await manager.ApplyPolicyAsync([new UsbGrantRecord(StickId, Start.AddHours(2))], Start);

        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeEnumerator(UsbDeviceInfo[] devices) : IUsbDeviceEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate() => devices;
    }

    private sealed class RecordingEnforcer : IUsbPolicyEnforcer
    {
        public List<(string Action, string InstanceId)> Calls { get; } = [];

        public UsbEnforcementResult Restrict(string instanceId) => Record("Restrict", instanceId);

        public UsbEnforcementResult AllowReadOnly(string instanceId) => Record("AllowReadOnly", instanceId);

        public UsbEnforcementResult AllowReadWrite(string instanceId) => Record("AllowReadWrite", instanceId);

        public UsbEnforcementResult Release(string instanceId) => Record("Release", instanceId);

        private UsbEnforcementResult Record(string action, string instanceId)
        {
            Calls.Add((action, instanceId));
            return UsbEnforcementResult.Ok;
        }
    }

    private sealed class FakeGrantStore : IUsbGrantStore
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

    private sealed class FakeLedger : IUsbRestrictionLedger
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
