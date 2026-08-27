using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Identity;
using Microsoft.Extensions.Logging.Abstractions;

// TestClock is a shared fake that happens to live in the USB tests' namespace.
// Imported rather than duplicated: a second copy would be one more thing to keep
// in step, and moving it would touch M11a test files for no functional gain.
using EndpointAgent.Core.Tests.Usb;

namespace EndpointAgent.Core.Tests.Identity;

/// <summary>
/// The agent's half of temporary administrator elevation.
/// </summary>
/// <remarks>
/// <para>
/// Two properties carry this feature, and most of the tests below exist to pin
/// one of them. <b>An account holds elevated rights only while a live, in-date
/// authorization names it</b> — so absence from the payload withdraws rights, and
/// an expired deadline withdraws them with no message arriving at all. And
/// <b>only accounts this agent elevated are ever lowered</b> — a machine's real
/// administrators are somebody else's decision.
/// </para>
/// <para>
/// The second property is why the ledger exists. Windows cannot distinguish an
/// administrator we created from one that was always there, so without a written
/// record the agent would either strip real administrators or leave every expired
/// elevation in force.
/// </para>
/// </remarks>
public sealed class LocalAdminElevationManagerTests
{
    private const string Machine = "S-1-5-21-9-9-9";
    private const string BuiltIn = Machine + "-500";
    private const string Sarah = Machine + "-1001";
    private const string Raj = Machine + "-1002";
    private const string Techsara = Machine + "-1003";

    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private const string AdministratorsSid = "S-1-5-32-544";
    private const string UsersSid = "S-1-5-32-545";

    private static LiveLocalAccount Account(string sid, string name, bool admin, bool enabled = true) =>
        new(sid, name, enabled, admin);

    private static (LocalAdminElevationManager Manager, FakeAccounts Accounts, FakeLedger Ledger, TestClock Clock)
        Build(params LiveLocalAccount[] accounts)
    {
        var control = new FakeAccounts(accounts);
        var ledger = new FakeLedger();
        var clock = new TestClock(Now);

        return (new LocalAdminElevationManager(
            control, ledger, clock, NullLogger<LocalAdminElevationManager>.Instance),
            control, ledger, clock);
    }

    private static ElevationGrant Grant(string sid, TimeSpan? within = null) =>
        new(sid, Now.Add(within ?? TimeSpan.FromHours(1)));

    // ---- raising -----------------------------------------------------------

    [Fact]
    public async Task An_authorized_standard_user_becomes_an_administrator()
    {
        var (manager, accounts, ledger, _) = Build(
            Account(BuiltIn, "Administrator", admin: true, enabled: false),
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        var outcome = await manager.ApplyAsync([Grant(Sarah)], Now);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Elevated.ShouldBe([Sarah]);
        accounts.IsAdministrator(Sarah).ShouldBeTrue();
        ledger.Saved.ShouldContain(Sarah);
    }

    [Fact]
    public async Task Several_authorized_accounts_are_elevated_together()
    {
        var (manager, accounts, _, _) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false),
            Account(Raj, "raj", admin: false));

        var outcome = await manager.ApplyAsync([Grant(Sarah), Grant(Raj)], Now);

        outcome.Elevated.ShouldBe([Sarah, Raj], ignoreOrder: true);
        accounts.IsAdministrator(Sarah).ShouldBeTrue();
        accounts.IsAdministrator(Raj).ShouldBeTrue();
    }

    /// <summary>
    /// An account that is already an administrator is not adopted.
    /// </summary>
    /// <remarks>
    /// It does not join the ledger, so when the elevation ends the agent does not
    /// lower rights it never granted. This is the case that would otherwise
    /// demote a machine's real administrator by way of an elevation nobody needed
    /// to apply.
    /// </remarks>
    [Fact]
    public async Task An_account_that_was_already_an_administrator_is_not_adopted()
    {
        var (manager, accounts, ledger, _) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([Grant(Techsara)], Now);

        ledger.Saved.ShouldNotContain(Techsara);
        accounts.Calls.ShouldBeEmpty();

        // And when the authorization ends, it is still an administrator.
        await manager.ApplyAsync([], Now.AddMinutes(1));
        accounts.IsAdministrator(Techsara).ShouldBeTrue();
    }

    // ---- lowering ----------------------------------------------------------

    [Fact]
    public async Task An_account_absent_from_the_set_is_returned_to_standard()
    {
        var (manager, accounts, _, clock) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([Grant(Sarah)], Now);
        accounts.IsAdministrator(Sarah).ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(5));
        var outcome = await manager.ApplyAsync([], Now.AddMinutes(5));

        outcome.Lowered.ShouldBe([Sarah]);
        accounts.IsAdministrator(Sarah).ShouldBeFalse();

        // The standard-user baseline is established before the removal, so the
        // account is never briefly in no group at all.
        accounts.Calls.ShouldContain((UsersSid, Sarah, true));
    }

    /// <summary>
    /// An expired grant is not applied, even if the server still sends it.
    /// </summary>
    /// <remarks>
    /// The server's sweeper is bookkeeping. The endpoint judges the deadline
    /// against its own clock, which is what makes an elevation lapse on time on a
    /// machine that has not heard from the server in days.
    /// </remarks>
    [Fact]
    public async Task An_expired_grant_is_not_applied_even_before_the_server_sweeps()
    {
        var (manager, accounts, _, _) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([new ElevationGrant(Sarah, Now.AddMinutes(-1))], Now);

        accounts.IsAdministrator(Sarah).ShouldBeFalse();
    }

    [Fact]
    public async Task Time_spent_offline_does_not_extend_an_elevation()
    {
        var (manager, accounts, _, clock) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        var grant = new ElevationGrant(Sarah, Now.AddHours(1));
        await manager.ApplyAsync([grant], Now);
        accounts.IsAdministrator(Sarah).ShouldBeTrue();

        // Offline for three hours; the same grant is re-delivered on reconnect.
        clock.Advance(TimeSpan.FromHours(3));
        await manager.ApplyAsync([grant], Now.AddHours(3));

        accounts.IsAdministrator(Sarah).ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_set_lowers_everything_this_agent_elevated_and_nothing_else()
    {
        var (manager, accounts, _, _) = Build(
            Account(BuiltIn, "Administrator", admin: true, enabled: false),
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([Grant(Sarah)], Now);
        await manager.ApplyAsync([], Now.AddMinutes(1));

        accounts.IsAdministrator(Sarah).ShouldBeFalse();
        accounts.IsAdministrator(Techsara).ShouldBeTrue();
        accounts.IsAdministrator(BuiltIn).ShouldBeTrue();
    }

    // ---- accounts that must never be touched -------------------------------

    /// <summary>
    /// An administrator this agent did not elevate is never lowered.
    /// </summary>
    /// <remarks>
    /// The property that makes reconciliation safe to run on a real machine. The
    /// first reconcile on any endpoint sees administrators it has no record of,
    /// and must leave every one of them alone.
    /// </remarks>
    [Fact]
    public async Task An_unrelated_administrator_is_never_lowered()
    {
        var (manager, accounts, _, _) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Raj, "raj", admin: true),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([], Now);

        accounts.IsAdministrator(Techsara).ShouldBeTrue();
        accounts.IsAdministrator(Raj).ShouldBeTrue();
        accounts.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_built_in_Administrator_is_never_modified()
    {
        var (manager, accounts, _, _) = Build(
            Account(BuiltIn, "Administrator", admin: true),
            Account(Techsara, "Techsara", admin: true));

        // Even asked directly, and even after being planted on the ledger.
        var outcome = await manager.ApplyAsync([Grant(BuiltIn)], Now);

        accounts.Calls.ShouldNotContain(c => c.Sid == BuiltIn);
        outcome.Refused.ShouldContain(r => r.Sid == BuiltIn);
    }

    /// <summary>
    /// The last enabled administrator is never lowered, even when authorized to be.
    /// </summary>
    /// <remarks>
    /// Evaluated against live Windows state read moments earlier, not against the
    /// server's inventory, because the answer to "is this the last one?" cannot
    /// be taken from a snapshot that is minutes old. Refusing leaves an account
    /// elevated past its window, which is reported rather than hidden — the
    /// alternative is a machine nobody can administer.
    /// </remarks>
    [Fact]
    public async Task The_last_enabled_administrator_is_not_lowered()
    {
        var (manager, accounts, _, _) = Build(
            Account(BuiltIn, "Administrator", admin: true, enabled: false),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([Grant(Sarah)], Now);
        accounts.IsAdministrator(Sarah).ShouldBeTrue();

        // Sarah is now the only ENABLED administrator: the built-in is disabled.
        var outcome = await manager.ApplyAsync([], Now.AddMinutes(1));

        outcome.Succeeded.ShouldBeFalse();
        outcome.Refused.ShouldContain(r => r.Sid == Sarah && r.Reason.Contains("last enabled administrator"));
        accounts.IsAdministrator(Sarah).ShouldBeTrue();
    }

    [Fact]
    public async Task An_account_that_does_not_exist_is_refused_rather_than_invented()
    {
        var (manager, accounts, _, _) = Build(Account(Techsara, "Techsara", admin: true));

        var outcome = await manager.ApplyAsync([Grant(Machine + "-4242")], Now);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Refused.ShouldContain(r => r.Reason.Contains("No local account"));
        accounts.Calls.ShouldBeEmpty();
    }

    // ---- stale tasks -------------------------------------------------------

    /// <summary>
    /// A task queued before a revocation cannot reinstate it by arriving after.
    /// </summary>
    /// <remarks>
    /// Entirely reachable: elevate an account, revoke it a minute later, and a
    /// laptop that was asleep for both receives the two tasks in whatever order
    /// it drains its queue. The issued-at check is the same protection
    /// ApplyUsbPolicy uses, deliberately rather than a second mechanism.
    /// </remarks>
    [Fact]
    public async Task A_stale_set_arriving_late_cannot_reinstate_a_revoked_elevation()
    {
        var (manager, accounts, _, _) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        // The revocation, issued second, arrives first.
        await manager.ApplyAsync([], Now.AddMinutes(5));

        // The older authorization turns up afterwards.
        var outcome = await manager.ApplyAsync([Grant(Sarah)], Now);

        accounts.IsAdministrator(Sarah).ShouldBeFalse();
        outcome.Elevated.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_set_issued_at_the_same_moment_is_still_applied()
    {
        var (manager, accounts, _, _) = Build(
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false));

        await manager.ApplyAsync([], Now);
        await manager.ApplyAsync([Grant(Sarah)], Now);

        accounts.IsAdministrator(Sarah).ShouldBeTrue();
    }

    // ---- verification ------------------------------------------------------

    /// <summary>
    /// A Windows call that did not throw is not evidence the membership changed.
    /// </summary>
    /// <remarks>
    /// Everything upstream — the task result, the console badge, the audit record
    /// — is derived from a re-read rather than from the assumption that the call
    /// worked. Without this the platform would report a green tick beside an
    /// account that never became an administrator.
    /// </remarks>
    [Fact]
    public async Task A_mutation_that_silently_did_nothing_is_reported_as_a_failure()
    {
        var control = new FakeAccounts([
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false),
        ])
        { SilentlyIgnoreChanges = true };

        var manager = new LocalAdminElevationManager(
            control, new FakeLedger(), new TestClock(Now),
            NullLogger<LocalAdminElevationManager>.Instance);

        var outcome = await manager.ApplyAsync([Grant(Sarah)], Now);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Refused.ShouldContain(r => r.Sid == Sarah && r.Reason.Contains("Verification failed"));
    }

    [Fact]
    public async Task A_windows_failure_surfaces_rather_than_being_swallowed()
    {
        var control = new FakeAccounts([
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false),
        ])
        { ThrowOn = Sarah };

        var manager = new LocalAdminElevationManager(
            control, new FakeLedger(), new TestClock(Now),
            NullLogger<LocalAdminElevationManager>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => manager.ApplyAsync([Grant(Sarah)], Now));
    }

    // ---- malformed input ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_grant_with_no_sid_is_dropped(string sid)
    {
        var (manager, accounts, _, _) = Build(Account(Techsara, "Techsara", admin: true));

        var outcome = await manager.ApplyAsync([new ElevationGrant(sid, Now.AddHours(1))], Now);

        outcome.Elevated.ShouldBeEmpty();
        accounts.Calls.ShouldBeEmpty();
    }

    // ---- restart persistence -----------------------------------------------

    /// <summary>
    /// A restarted agent still knows which accounts are its to lower.
    /// </summary>
    /// <remarks>
    /// The ledger is the only thing carrying that knowledge across a restart; the
    /// manager's in-memory view is gone. Without it, a restart would turn every
    /// outstanding elevation into a permanent one.
    /// </remarks>
    [Fact]
    public async Task A_restarted_agent_still_lowers_what_it_elevated()
    {
        var control = new FakeAccounts([
            Account(Techsara, "Techsara", admin: true),
            Account(Sarah, "sarah", admin: false),
        ]);
        var ledger = new FakeLedger();

        var first = new LocalAdminElevationManager(
            control, ledger, new TestClock(Now), NullLogger<LocalAdminElevationManager>.Instance);
        await first.ApplyAsync([Grant(Sarah)], Now);
        control.IsAdministrator(Sarah).ShouldBeTrue();

        // A new manager over the same machine and the same ledger: the restart.
        var restarted = new LocalAdminElevationManager(
            control, ledger, new TestClock(Now.AddMinutes(1)),
            NullLogger<LocalAdminElevationManager>.Instance);

        await restarted.ApplyAsync([], Now.AddMinutes(1));

        control.IsAdministrator(Sarah).ShouldBeFalse();
    }

    // ---- fakes -------------------------------------------------------------

    /// <summary>The machine's account state, and a record of what was asked of it.</summary>
    private sealed class FakeAccounts(LiveLocalAccount[] seed) : ILocalAccountsControl
    {
        private readonly List<LiveLocalAccount> _accounts = [.. seed];

        public List<(string Group, string Sid, bool IsMember)> Calls { get; } = [];

        /// <summary>Accepts the call and changes nothing, as a failing driver would.</summary>
        public bool SilentlyIgnoreChanges { get; init; }

        public string? ThrowOn { get; init; }

        public bool IsAdministrator(string sid) =>
            _accounts.Single(a => a.Sid == sid).IsAdministrator;

        public ValueTask SetGroupMembershipAsync(
            string groupSid, string memberSid, bool isMember, CancellationToken cancellationToken = default)
        {
            Calls.Add((groupSid, memberSid, isMember));

            if (ThrowOn is not null && string.Equals(ThrowOn, memberSid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Access denied.");
            }

            if (SilentlyIgnoreChanges || groupSid != AdministratorsSid)
            {
                return ValueTask.CompletedTask;
            }

            var index = _accounts.FindIndex(a => a.Sid == memberSid);
            if (index >= 0)
            {
                _accounts[index] = _accounts[index] with { IsAdministrator = isMember };
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<LiveLocalAccount>> GetLiveAccountsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<LiveLocalAccount>>([.. _accounts]);

        // Not part of elevation; this manager must never call them.
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
        public IReadOnlyCollection<string> Saved { get; private set; } = [];

        public ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Saved);

        public ValueTask SaveAsync(IReadOnlyCollection<string> sids, CancellationToken cancellationToken = default)
        {
            Saved = sids;
            return ValueTask.CompletedTask;
        }
    }
}
