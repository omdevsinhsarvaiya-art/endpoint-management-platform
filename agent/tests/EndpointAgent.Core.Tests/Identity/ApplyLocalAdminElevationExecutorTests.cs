using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Identity;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using EndpointAgent.Core.Tests.Usb;

namespace EndpointAgent.Core.Tests.Identity;

/// <summary>
/// How the executor handles payloads that are wrong in every way a payload can
/// be wrong.
/// </summary>
/// <remarks>
/// The asymmetry worth stating: a payload that fails to parse leaves the previous
/// set alone, while an individual entry that fails to parse is dropped. Both
/// choices narrow rather than widen — a corrupted message cannot revoke a
/// legitimate elevation, and a bad entry cannot create one.
/// </remarks>
public sealed class ApplyLocalAdminElevationExecutorTests
{
    private const string Machine = "S-1-5-21-3-3-3";
    private const string Sarah = Machine + "-1001";
    private const string Techsara = Machine + "-1002";

    private static readonly DateTimeOffset Start = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static (ApplyLocalAdminElevationExecutor Executor, FakeAccounts Accounts) Build()
    {
        var accounts = new FakeAccounts();
        var clock = new TestClock(Start);

        var manager = new LocalAdminElevationManager(
            accounts, new FakeLedger(), clock, NullLogger<LocalAdminElevationManager>.Instance);

        return (new ApplyLocalAdminElevationExecutor(
            manager, clock, NullLogger<ApplyLocalAdminElevationExecutor>.Instance), accounts);
    }

    private static AgentTask Task(string? payload) =>
        new(Guid.CreateVersion7(), "ApplyLocalAdminElevation", payload);

    private static string Payload(string elevations, string issuedAt = "2026-08-28T12:00:00+00:00") =>
        $$"""{"elevations":[{{elevations}}],"issuedAt":"{{issuedAt}}"}""";

    private static string Entry(string sid, string expiresAt = "2026-08-28T14:00:00+00:00") =>
        $$"""{"sid":"{{sid}}","expiresAt":"{{expiresAt}}"}""";

    [Fact]
    public void The_executor_answers_to_the_server_task_type_name()
    {
        Build().Executor.TaskType.ShouldBe("ApplyLocalAdminElevation");
    }

    [Fact]
    public async Task A_well_formed_elevation_is_applied_and_reported_with_evidence()
    {
        var (executor, accounts) = Build();

        var result = await executor.ExecuteAsync(Task(Payload(Entry(Sarah))));

        result.Succeeded.ShouldBeTrue();
        accounts.IsAdministrator(Sarah).ShouldBeTrue();

        // Structured evidence naming the account, not just a prose message: the
        // console has to distinguish "applied" from "refused" per account.
        result.ResultJson.ShouldNotBeNull();
        result.ResultJson!.ShouldContain(Sarah);
        result.ResultJson.ShouldContain("elevated");
    }

    [Fact]
    public async Task An_empty_set_is_valid_and_means_nobody_is_authorized()
    {
        var (executor, accounts) = Build();

        await executor.ExecuteAsync(Task(Payload(Entry(Sarah))));
        accounts.IsAdministrator(Sarah).ShouldBeTrue();

        var result = await executor.ExecuteAsync(
            Payload("", "2026-08-28T12:30:00+00:00") is var p ? Task(p) : null!);

        result.Succeeded.ShouldBeTrue();
        accounts.IsAdministrator(Sarah).ShouldBeFalse();
    }

    /// <summary>
    /// A malformed payload leaves the previous set in force.
    /// </summary>
    /// <remarks>
    /// Deliberately not treated as an empty set. "The message was garbage" is not
    /// evidence that an administrator revoked anything, and silently lowering on a
    /// bad parse would let a corrupted task strip a legitimate elevation.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"issuedAt":"2026-08-28T12:00:00+00:00"}""")]
    [InlineData("""{"elevations":"nope","issuedAt":"2026-08-28T12:00:00+00:00"}""")]
    [InlineData("""{"elevations":[]}""")]
    public async Task A_malformed_payload_is_refused_without_touching_any_account(string? payload)
    {
        var (executor, accounts) = Build();

        await executor.ExecuteAsync(Task(Payload(Entry(Sarah))));
        accounts.Calls.Clear();

        var result = await executor.ExecuteAsync(Task(payload));

        result.Succeeded.ShouldBeFalse();
        accounts.Calls.ShouldBeEmpty();
        accounts.IsAdministrator(Sarah).ShouldBeTrue();
    }

    /// <summary>
    /// A malformed entry is dropped; the rest of the message still applies.
    /// </summary>
    [Theory]
    [InlineData("""{"expiresAt":"2026-08-28T14:00:00+00:00"}""")]           // no sid
    [InlineData("""{"sid":"","expiresAt":"2026-08-28T14:00:00+00:00"}""")]
    [InlineData("""{"sid":123,"expiresAt":"2026-08-28T14:00:00+00:00"}""")] // wrong type
    [InlineData("""{"sid":"S-1-5-21-3-3-3-1001"}""")]                       // no expiry
    [InlineData("""{"sid":"S-1-5-21-3-3-3-1001","expiresAt":42}""")]
    [InlineData("\"a string, not an object\"")]
    public async Task A_bad_entry_is_dropped_without_voiding_the_good_ones(string bad)
    {
        var (executor, accounts) = Build();

        var result = await executor.ExecuteAsync(Task(Payload($"{bad},{Entry(Techsara)}")));

        result.Succeeded.ShouldBeTrue();
        accounts.IsAdministrator(Techsara).ShouldBeTrue();
        accounts.IsAdministrator(Sarah).ShouldBeFalse();
    }

    [Fact]
    public async Task An_already_expired_entry_is_dropped_at_parse_time()
    {
        var (executor, accounts) = Build();

        var result = await executor.ExecuteAsync(
            Task(Payload(Entry(Sarah, "2026-08-28T11:00:00+00:00"))));

        result.Succeeded.ShouldBeTrue();
        accounts.IsAdministrator(Sarah).ShouldBeFalse();
    }

    [Fact]
    public async Task An_absurd_number_of_entries_is_refused_outright()
    {
        var (executor, accounts) = Build();

        var entries = string.Join(',', Enumerable
            .Range(0, ApplyLocalAdminElevationExecutor.MaxElevations + 1)
            .Select(i => Entry($"{Machine}-{9000 + i}")));

        var result = await executor.ExecuteAsync(Task(Payload(entries)));

        result.Succeeded.ShouldBeFalse();
        accounts.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// Expiry is judged by the injected clock, not the wall clock.
    /// </summary>
    /// <remarks>
    /// The same payload twice with only the clock moved. This pins the property
    /// that behaviour depends on injected time and nothing else — the USB
    /// executor shipped once reading <c>DateTimeOffset.UtcNow</c>, passing
    /// locally in the morning and failing in CI once the day had moved past the
    /// fixture.
    /// </remarks>
    [Fact]
    public async Task Whether_a_grant_is_expired_is_decided_by_the_injected_clock()
    {
        var accounts = new FakeAccounts();
        var clock = new TestClock(Start);
        var manager = new LocalAdminElevationManager(
            accounts, new FakeLedger(), clock, NullLogger<LocalAdminElevationManager>.Instance);
        var executor = new ApplyLocalAdminElevationExecutor(
            manager, clock, NullLogger<ApplyLocalAdminElevationExecutor>.Instance);

        await executor.ExecuteAsync(Task(Payload(Entry(Sarah))));
        accounts.IsAdministrator(Sarah).ShouldBeTrue();

        clock.Advance(TimeSpan.FromHours(3));
        await executor.ExecuteAsync(Task(Payload(Entry(Sarah), "2026-08-28T15:30:00+00:00")));

        accounts.IsAdministrator(Sarah).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refusal_makes_the_task_fail_and_names_the_account()
    {
        var (executor, _) = Build();

        var result = await executor.ExecuteAsync(Task(Payload(Entry(Machine + "-4242"))));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.ResultJson!.ShouldContain("refused");
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeAccounts : ILocalAccountsControl
    {
        private const string AdministratorsSid = "S-1-5-32-544";

        private readonly List<LiveLocalAccount> _accounts =
        [
            // A pre-existing administrator, so the last-admin rule never fires
            // and these tests measure parsing rather than safety refusals.
            new(Machine + "-500", "Administrator", true, true),
            new(Sarah, "sarah", true, false),
            new(Techsara, "Techsara", true, false),
        ];

        public List<(string Group, string Sid, bool IsMember)> Calls { get; } = [];

        public bool IsAdministrator(string sid) => _accounts.Single(a => a.Sid == sid).IsAdministrator;

        public ValueTask SetGroupMembershipAsync(
            string groupSid, string memberSid, bool isMember, CancellationToken cancellationToken = default)
        {
            Calls.Add((groupSid, memberSid, isMember));

            if (groupSid == AdministratorsSid)
            {
                var i = _accounts.FindIndex(a => a.Sid == memberSid);
                if (i >= 0)
                {
                    _accounts[i] = _accounts[i] with { IsAdministrator = isMember };
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<LiveLocalAccount>> GetLiveAccountsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<LiveLocalAccount>>([.. _accounts]);

        public ValueTask<CreatedLocalAccount> CreateUserAsync(
            string username, string password, string? fullName, string? description, bool enabled,
            bool mustChangePassword, bool administrator, IReadOnlyList<string>? additionalGroups,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DeleteUserAsync(string sid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetUserEnabledAsync(string sid, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetPasswordAsync(string sid, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ForcePasswordChangeAsync(string sid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLedger : IElevationLedger
    {
        private IReadOnlyCollection<string> _sids = [];

        public ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_sids);

        public ValueTask SaveAsync(IReadOnlyCollection<string> sids, CancellationToken cancellationToken = default)
        {
            _sids = sids;
            return ValueTask.CompletedTask;
        }
    }
}
