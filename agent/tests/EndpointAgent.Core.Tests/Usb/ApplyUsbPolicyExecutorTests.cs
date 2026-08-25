using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointAgent.Core.Usb;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// How the executor handles payloads that are wrong in every way a payload can
/// be wrong.
/// </summary>
/// <remarks>
/// The asymmetry worth stating: a payload that fails to parse leaves the
/// previous policy alone, while an individual grant entry that fails to parse is
/// dropped. Both choices narrow rather than widen — a bad message cannot cancel
/// a legitimate grant, and a bad entry cannot create one.
/// </remarks>
public sealed class ApplyUsbPolicyExecutorTests
{
    private const string StickId = @"USB\VID_0781&PID_5581\ABC123";

    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static (ApplyUsbPolicyExecutor Executor, RecordingEnforcer Enforcer) Build()
    {
        var enforcer = new RecordingEnforcer();

        // One clock shared by the manager and the executor, fixed at Start. The
        // fixtures below carry absolute timestamps around Start, so without an
        // injected clock these tests would quietly change meaning as the real
        // day advanced past them.
        var clock = new TestClock(Start);

        var manager = new UsbPolicyManager(
            new SingleStorageEnumerator(StickId),
            enforcer,
            new InMemoryGrantStore(),
            new InMemoryLedger(),
            clock,
            NullLogger<UsbPolicyManager>.Instance);

        return (new ApplyUsbPolicyExecutor(manager, clock, NullLogger<ApplyUsbPolicyExecutor>.Instance), enforcer);
    }

    private static AgentTask Task(string? payload) =>
        new(Guid.CreateVersion7(), "ApplyUsbPolicy", payload);

    [Fact]
    public void The_executor_answers_to_the_server_task_type_name()
    {
        var (executor, _) = Build();
        executor.TaskType.ShouldBe("ApplyUsbPolicy");
    }

    [Fact]
    public async Task A_well_formed_grant_is_applied_and_reported_as_success()
    {
        var (executor, enforcer) = Build();

        var result = await executor.ExecuteAsync(Task($$"""
            {"grants":[{"instanceId":"{{StickId.Replace(@"\", @"\\")}}",
             "policy":"ReadOnly","expiresAt":"2026-08-25T14:00:00+00:00"}],
             "issuedAt":"2026-08-25T12:00:00+00:00"}
            """));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);
    }

    [Fact]
    public async Task An_empty_grant_list_restricts_and_succeeds()
    {
        var (executor, enforcer) = Build();

        var result = await executor.ExecuteAsync(
            Task("""{"grants":[],"issuedAt":"2026-08-25T12:00:00+00:00"}"""));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"issuedAt":"2026-08-25T12:00:00+00:00"}""")]
    [InlineData("""{"grants":"nope","issuedAt":"2026-08-25T12:00:00+00:00"}""")]
    [InlineData("""{"grants":[]}""")]
    public async Task A_malformed_payload_is_refused_without_touching_any_device(string? payload)
    {
        var (executor, enforcer) = Build();

        var result = await executor.ExecuteAsync(Task(payload));

        result.Succeeded.ShouldBeFalse();

        // Nothing was enforced either way: the previously cached policy stands.
        // A corrupted message is not evidence that a grant was revoked.
        enforcer.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// A grant naming a policy this agent does not implement is discarded.
    /// </summary>
    /// <remarks>
    /// The parameter cases are the ones that matter: <c>ReadWrite</c> and
    /// <c>FullAccess</c> do not exist anywhere in the platform, and a payload
    /// inventing them must not be rounded up to "some access". It is rounded
    /// down to none.
    /// </remarks>
    [Theory]
    [InlineData("ReadWrite")]
    [InlineData("FullAccess")]
    [InlineData("Unrestricted")]
    [InlineData("")]
    public async Task A_grant_naming_an_unknown_policy_is_dropped(string policy)
    {
        var (executor, enforcer) = Build();

        var result = await executor.ExecuteAsync(Task($$"""
            {"grants":[{"instanceId":"{{StickId.Replace(@"\", @"\\")}}",
             "policy":"{{policy}}","expiresAt":"2026-08-25T14:00:00+00:00"}],
             "issuedAt":"2026-08-25T12:00:00+00:00"}
            """));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    /// <summary>
    /// The enum-as-number regression, caught here rather than on a real machine.
    /// </summary>
    /// <remarks>
    /// This is exactly the shape that broke service control once already: the
    /// server serialising an enum by ordinal while the agent reads it as a
    /// string. If it recurred for USB the grant would be silently dropped, so
    /// the behaviour is pinned — dropped, not honoured, and definitely not
    /// crashed on.
    /// </remarks>
    [Fact]
    public async Task A_numeric_policy_value_is_dropped_rather_than_guessed_at()
    {
        var (executor, enforcer) = Build();

        var result = await executor.ExecuteAsync(Task($$"""
            {"grants":[{"instanceId":"{{StickId.Replace(@"\", @"\\")}}",
             "policy":0,"expiresAt":"2026-08-25T14:00:00+00:00"}],
             "issuedAt":"2026-08-25T12:00:00+00:00"}
            """));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task A_grant_with_no_expiry_is_dropped()
    {
        var (executor, enforcer) = Build();

        var result = await executor.ExecuteAsync(Task($$"""
            {"grants":[{"instanceId":"{{StickId.Replace(@"\", @"\\")}}","policy":"ReadOnly"}],
             "issuedAt":"2026-08-25T12:00:00+00:00"}
            """));

        result.Succeeded.ShouldBeTrue();
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    /// <summary>
    /// Expiry is judged against the injected clock, not the wall clock.
    /// </summary>
    /// <remarks>
    /// The same payload, twice, with only the clock moved: honoured while the
    /// deadline is ahead, dropped once it is behind. This pins the property that
    /// the executor's behaviour depends on the injected time and nothing else —
    /// the first version read <c>DateTimeOffset.UtcNow</c>, which passed locally
    /// in the morning and failed in CI once the real day had moved past the
    /// fixture's timestamp.
    /// </remarks>
    [Fact]
    public async Task Whether_a_grant_is_expired_is_decided_by_the_injected_clock()
    {
        const string payload = """
            {"grants":[{"instanceId":"USB\\VID_0781&PID_5581\\ABC123",
             "policy":"ReadOnly","expiresAt":"2026-08-25T14:00:00+00:00"}],
             "issuedAt":"2026-08-25T12:00:00+00:00"}
            """;

        var enforcer = new RecordingEnforcer();
        var clock = new TestClock(Start);
        var manager = new UsbPolicyManager(
            new SingleStorageEnumerator(StickId), enforcer, new InMemoryGrantStore(),
            new InMemoryLedger(), clock, NullLogger<UsbPolicyManager>.Instance);
        var executor = new ApplyUsbPolicyExecutor(
            manager, clock, NullLogger<ApplyUsbPolicyExecutor>.Instance);

        // 12:00 — the deadline is two hours away.
        await executor.ExecuteAsync(Task(payload));
        enforcer.Calls.ShouldBe([("AllowReadOnly", StickId)]);

        // 15:00 — same payload, deadline an hour in the past.
        enforcer.Calls.Clear();
        clock.Advance(TimeSpan.FromHours(3));
        await executor.ExecuteAsync(Task(payload));
        enforcer.Calls.ShouldBe([("Restrict", StickId)]);
    }

    [Fact]
    public async Task An_absurd_number_of_grants_is_refused_outright()
    {
        var (executor, enforcer) = Build();

        var entries = string.Join(',', Enumerable.Range(0, ApplyUsbPolicyExecutor.MaxGrants + 1)
            .Select(i => $$"""{"instanceId":"USB\\DEV{{i}}","policy":"ReadOnly","expiresAt":"2026-08-25T14:00:00+00:00"}"""));

        var result = await executor.ExecuteAsync(
            Task($$"""{"grants":[{{entries}}],"issuedAt":"2026-08-25T12:00:00+00:00"}"""));

        result.Succeeded.ShouldBeFalse();
        enforcer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_device_that_cannot_be_enforced_makes_the_task_fail()
    {
        var enforcer = new RecordingEnforcer { FailWith = "access denied" };
        var clock = new TestClock(Start);

        var manager = new UsbPolicyManager(
            new SingleStorageEnumerator(StickId), enforcer, new InMemoryGrantStore(),
            new InMemoryLedger(), clock, NullLogger<UsbPolicyManager>.Instance);

        var executor = new ApplyUsbPolicyExecutor(manager, clock, NullLogger<ApplyUsbPolicyExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Task("""{"grants":[],"issuedAt":"2026-08-25T12:00:00+00:00"}"""));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldNotBeNull();

        // The task result must say so rather than reporting a control that is
        // not actually in place.
        result.Message!.ShouldContain("could not be enforced");
    }

    private sealed class SingleStorageEnumerator(string instanceId) : IUsbDeviceEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate() =>
        [
            new(instanceId, UsbClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer", null, true),
        ];
    }

    private sealed class RecordingEnforcer : IUsbPolicyEnforcer
    {
        public List<(string Action, string InstanceId)> Calls { get; } = [];

        public string? FailWith { get; init; }

        public UsbEnforcementResult Restrict(string instanceId) => Record("Restrict", instanceId);

        public UsbEnforcementResult AllowReadOnly(string instanceId) => Record("AllowReadOnly", instanceId);

        public UsbEnforcementResult Release(string instanceId) => Record("Release", instanceId);

        private UsbEnforcementResult Record(string action, string instanceId)
        {
            Calls.Add((action, instanceId));
            return FailWith is null ? UsbEnforcementResult.Ok : UsbEnforcementResult.Failed(FailWith);
        }
    }

    private sealed class InMemoryLedger : IUsbRestrictionLedger
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

    private sealed class InMemoryGrantStore : IUsbGrantStore
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
}
