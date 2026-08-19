using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The local-account executors' decisions, proven against a fake control layer so no
/// Windows account is touched. Covers the promote/demote path, the live last-admin
/// re-check, malformed payloads, and the password-handling discipline.
/// </summary>
public sealed class LocalAccountExecutorTests
{
    private const string AdminsSid = "S-1-5-32-544";
    private const string TargetSid = "S-1-5-21-1-2-3-1001";
    private const string OtherAdminSid = "S-1-5-21-1-2-3-1002";
    private const string BuiltInAdminSid = "S-1-5-21-1-2-3-500";

    private static AgentTask MakeTask(string type, object payload) =>
        new(Guid.CreateVersion7(), type, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    // ------------------------------------------------- change account type

    [Fact]
    public async Task Promoting_adds_the_account_to_the_administrators_group()
    {
        var control = new FakeControl();
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("ChangeLocalUserType", new { sid = TargetSid, username = "Techsara", administrator = true }));

        result.Succeeded.ShouldBeTrue();
        control.MembershipCalls.ShouldHaveSingleItem();
        control.MembershipCalls[0].GroupSid.ShouldBe(AdminsSid, "promotion must target BUILTIN\\Administrators");
        control.MembershipCalls[0].MemberSid.ShouldBe(TargetSid);
        control.MembershipCalls[0].IsMember.ShouldBeTrue();
        (result.Message ?? "").ShouldContain("Administrator");
    }

    [Fact]
    public async Task Demoting_removes_the_account_from_the_administrators_group()
    {
        var control = new FakeControl
        {
            LiveAccounts =
            [
                new LiveLocalAccount(TargetSid, "Techsara", true, true),
                new LiveLocalAccount(OtherAdminSid, "Admin", true, true),
            ],
        };
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("ChangeLocalUserType", new { sid = TargetSid, username = "Techsara", administrator = false }));

        result.Succeeded.ShouldBeTrue();

        // Demotion now also establishes the BUILTIN\\Users baseline, so this is two
        // calls rather than one. The removal is what this test is about.
        var removals = control.MembershipCalls.Where(c => !c.IsMember).ToList();
        removals.ShouldHaveSingleItem().GroupSid.ShouldBe(AdminsSid);
        (result.Message ?? "").ShouldContain("Standard User");
    }

    [Fact]
    public async Task Demoting_the_last_enabled_administrator_is_refused_against_live_state()
    {
        // The server may have queued this from stale inventory; the live check is what
        // actually protects the machine.
        var control = new FakeControl
        {
            LiveAccounts = [new LiveLocalAccount(TargetSid, "OnlyAdmin", true, true)],
        };
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("ChangeLocalUserType", new { sid = TargetSid, username = "OnlyAdmin", administrator = false }));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("no enabled administrator");
        control.MembershipCalls.ShouldBeEmpty("nothing may be changed once the guard trips");
    }

    [Fact]
    public async Task A_malformed_change_type_payload_fails_without_touching_windows()
    {
        var control = new FakeControl();
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        var result = await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ChangeLocalUserType", "{ not json"));

        result.Succeeded.ShouldBeFalse();
        control.MembershipCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_payload_fails_without_touching_windows()
    {
        var control = new FakeControl();
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        var result = await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ChangeLocalUserType", null));

        result.Succeeded.ShouldBeFalse();
        control.MembershipCalls.ShouldBeEmpty();
    }

    // ------------------------------------------------------ enable/disable

    [Fact]
    public async Task Enabling_sets_the_account_enabled()
    {
        var control = new FakeControl();
        var executor = new EnableLocalUserExecutor(control, NullLogger<EnableLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("EnableLocalUser", new { sid = TargetSid, username = "Techsara", enabled = true }));

        result.Succeeded.ShouldBeTrue();
        control.EnabledCalls.ShouldHaveSingleItem();
        control.EnabledCalls[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Disabling_the_last_enabled_administrator_is_refused()
    {
        var control = new FakeControl
        {
            LiveAccounts = [new LiveLocalAccount(TargetSid, "OnlyAdmin", true, true)],
        };
        var executor = new DisableLocalUserExecutor(control, NullLogger<DisableLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("DisableLocalUser", new { sid = TargetSid, username = "OnlyAdmin", enabled = false }));

        result.Succeeded.ShouldBeFalse();
        control.EnabledCalls.ShouldBeEmpty();
    }

    // ------------------------------------------------------------- delete

    [Fact]
    public async Task Deleting_the_built_in_administrator_is_refused()
    {
        var control = new FakeControl
        {
            LiveAccounts =
            [
                new LiveLocalAccount(BuiltInAdminSid, "Administrator", true, true),
                new LiveLocalAccount(OtherAdminSid, "Admin", true, true),
            ],
        };
        var executor = new DeleteLocalUserExecutor(control, NullLogger<DeleteLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("DeleteLocalUser", new { sid = BuiltInAdminSid, username = "Administrator" }));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("protected");
        control.DeletedSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_a_standard_user_succeeds()
    {
        var control = new FakeControl
        {
            LiveAccounts =
            [
                new LiveLocalAccount(TargetSid, "Temp", true, false),
                new LiveLocalAccount(OtherAdminSid, "Admin", true, true),
            ],
        };
        var executor = new DeleteLocalUserExecutor(control, NullLogger<DeleteLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("DeleteLocalUser", new { sid = TargetSid, username = "Temp" }));

        result.Succeeded.ShouldBeTrue();
        control.DeletedSids.ShouldHaveSingleItem().ShouldBe(TargetSid);
    }

    // ----------------------------------------------------- group membership

    [Fact]
    public async Task Adding_a_group_member_calls_through_with_both_sids()
    {
        var control = new FakeControl();
        var executor = new AddLocalUserToGroupExecutor(control, NullLogger<AddLocalUserToGroupExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("AddLocalUserToGroup", new
        {
            groupSid = "S-1-5-32-555", groupName = "Remote Desktop Users",
            memberSid = TargetSid, memberName = "Techsara",
        }));

        result.Succeeded.ShouldBeTrue();
        control.MembershipCalls.ShouldHaveSingleItem();
        control.MembershipCalls[0].GroupSid.ShouldBe("S-1-5-32-555");
        control.MembershipCalls[0].IsMember.ShouldBeTrue();
    }

    [Fact]
    public async Task Removing_the_last_admin_from_the_administrators_group_is_refused()
    {
        // Removing from Administrators is a demotion by another name and earns the
        // same protection as an explicit account-type change.
        var control = new FakeControl
        {
            LiveAccounts = [new LiveLocalAccount(TargetSid, "OnlyAdmin", true, true)],
        };
        var executor = new RemoveLocalUserFromGroupExecutor(
            control, NullLogger<RemoveLocalUserFromGroupExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("RemoveLocalUserFromGroup", new
        {
            groupSid = AdminsSid, groupName = "Administrators",
            memberSid = TargetSid, memberName = "OnlyAdmin",
        }));

        result.Succeeded.ShouldBeFalse();
        control.MembershipCalls.ShouldBeEmpty();
    }

    // -------------------------------------------------------- password paths

    [Fact]
    public async Task Creating_a_user_redeems_the_secret_and_never_echoes_it()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = "SuperSecret123!" };
        var executor = new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("CreateLocalUser", new
        {
            username = "EndpointTestUser", fullName = "Test", description = "d",
            secretRef = "ref-123", enabled = true, mustChangePasswordAtNextLogon = true,
        }));

        result.Succeeded.ShouldBeTrue();
        control.CreatedUsers.ShouldHaveSingleItem().Username.ShouldBe("EndpointTestUser");
        control.CreatedUsers[0].Password.ShouldBe("SuperSecret123!", "the agent must apply the real password");

        // The secret must never travel back to the server in the result.
        (result.Message ?? "").ShouldNotContain("SuperSecret123!");
        (result.ResultJson ?? "").ShouldNotContain("SuperSecret123!");
    }

    [Fact]
    public async Task Creating_with_must_change_password_passes_the_flag_through()
    {
        // Regression: this combination failed live because the Windows implementation
        // used NetUserSetInfo level 1017 (acct_expires) instead of the
        // UF_PASSWORD_EXPIRED flag, so Windows refused the whole create.
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = "SuperSecret123!" };
        var executor = new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("CreateLocalUser", new
        {
            username = "test", fullName = "test", secretRef = "ref-1",
            enabled = true, mustChangePasswordAtNextLogon = true,
        }));

        result.Succeeded.ShouldBeTrue();
        control.CreatedUsers.ShouldHaveSingleItem().MustChange.ShouldBeTrue();
    }

    [Fact]
    public async Task A_group_this_device_does_not_have_is_reported_but_does_not_fail_the_create()
    {
        // Regression: creating an IT administrator failed outright on a Windows SKU
        // with no "Remote Desktop Users" group, and the whole account was rolled back
        // over an optional extra. The account is the point; the optional group is not.
        var control = new FakeControl
        {
            CreateResult = new CreatedLocalAccount(
                "S-1-5-21-1-2-3-4242", "EndpointAdminTest", Enabled: true, IsAdministrator: true,
                Groups: ["Administrators", "Users"],
                SkippedGroups: ["Remote Desktop Users"], IsInUsersGroup: true),
        };
        var secrets = new FakeSecrets { Secret = "SuperSecret123!" };
        var executor = new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("CreateLocalUser", new
        {
            username = "EndpointAdminTest", secretRef = "ref-1", enabled = true,
            administrator = true, additionalGroups = new[] { "Remote Desktop Users" },
        }));

        result.Succeeded.ShouldBeTrue("a missing optional group must not destroy a correct account");

        // ...but the operator must be told, in the message itself. A skip they only
        // find by reading result JSON is a silent one.
        (result.Message ?? "").ShouldContain("Remote Desktop Users");
        (result.Message ?? "").ShouldContain("no such group on this device");
        (result.ResultJson ?? "").ShouldContain("skippedGroups");
    }

    [Fact]
    public async Task A_fully_applied_create_reports_no_skipped_groups()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = "SuperSecret123!" };
        var executor = new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("CreateLocalUser", new
        {
            username = "PlainUser", secretRef = "ref-1", enabled = true,
        }));

        result.Succeeded.ShouldBeTrue();
        // Nothing was skipped, so nothing should be reported as skipped.
        (result.Message ?? "").ShouldNotContain("no such group");
    }

    [Fact]
    public async Task A_created_standard_user_reports_its_users_group_membership()
    {
        // Regression: a created standard user had NO local group memberships at all.
        // It still authenticated, because BUILTIN\Users contains Authenticated Users,
        // so nothing looked wrong until someone ran `net user` on the account.
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = "SuperSecret123!" };
        var executor = new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("CreateLocalUser", new
        {
            username = "StandardUser1", secretRef = "ref-1", enabled = true,
        }));

        result.Succeeded.ShouldBeTrue();
        (result.ResultJson ?? "").ShouldContain("isInUsersGroup");

        using var json = JsonDocument.Parse(result.ResultJson!);
        json.RootElement.GetProperty("isInUsersGroup").GetBoolean()
            .ShouldBeTrue("a created account must be reported as a member of BUILTIN\\Users");
        json.RootElement.GetProperty("isAdministrator").GetBoolean()
            .ShouldBeFalse("a standard user must never be an administrator");
    }

    [Fact]
    public async Task Demoting_an_administrator_establishes_the_users_baseline_first()
    {
        // An account whose only membership was Administrators would otherwise come out
        // of a demotion belonging to no local group at all.
        var control = new FakeControl
        {
            LiveAccounts =
            [
                new LiveLocalAccount("S-1-5-21-1-2-3-1010", "Demoted", Enabled: true, IsAdministrator: true),
                new LiveLocalAccount("S-1-5-21-1-2-3-500", "Administrator", Enabled: true, IsAdministrator: true),
            ],
        };
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("ChangeLocalUserType", new
        {
            sid = "S-1-5-21-1-2-3-1010", username = "Demoted", administrator = false,
        }));

        result.Succeeded.ShouldBeTrue();

        var addedToUsers = control.MembershipCalls
            .FindIndex(c => c.GroupSid == "S-1-5-32-545" && c.IsMember);
        var removedFromAdmins = control.MembershipCalls
            .FindIndex(c => c.GroupSid == "S-1-5-32-544" && !c.IsMember);

        addedToUsers.ShouldBeGreaterThanOrEqualTo(0, "demotion must establish the Users baseline");
        removedFromAdmins.ShouldBeGreaterThanOrEqualTo(0, "demotion must remove Administrators membership");
        addedToUsers.ShouldBeLessThan(removedFromAdmins,
            "Users must be joined BEFORE Administrators is dropped, so the account is never in neither");
    }

    [Fact]
    public async Task Promoting_does_not_touch_the_users_baseline()
    {
        // Promotion is additive; it has no business editing the baseline.
        var control = new FakeControl();
        var executor = new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance);

        await executor.ExecuteAsync(MakeTask("ChangeLocalUserType", new
        {
            sid = "S-1-5-21-1-2-3-1010", username = "Promoted", administrator = true,
        }));

        control.MembershipCalls.ShouldAllBe(c => c.GroupSid == "S-1-5-32-544");
    }

    [Fact]
    public async Task An_unredeemable_secret_fails_the_task_without_creating_anything()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = null }; // expired, replayed, or another device's
        var executor = new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("CreateLocalUser", new
        {
            username = "EndpointTestUser", secretRef = "stale-ref", enabled = true,
            mustChangePasswordAtNextLogon = false,
        }));

        result.Succeeded.ShouldBeFalse();
        control.CreatedUsers.ShouldBeEmpty("a task that cannot get its secret must change nothing");
    }

    [Fact]
    public async Task Resetting_a_password_never_puts_the_secret_in_the_result()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = "AnotherSecret456!" };
        var executor = new ResetLocalUserPasswordExecutor(
            control, secrets, NullLogger<ResetLocalUserPasswordExecutor>.Instance);

        var result = await executor.ExecuteAsync(MakeTask("ResetLocalUserPassword", new
        {
            sid = TargetSid, username = "Techsara", secretRef = "ref-456",
        }));

        result.Succeeded.ShouldBeTrue();
        control.PasswordResets.ShouldHaveSingleItem().Password.ShouldBe("AnotherSecret456!");
        (result.Message ?? "").ShouldNotContain("AnotherSecret456!");
        (result.ResultJson ?? "").ShouldNotContain("AnotherSecret456!");
    }

    [Fact]
    public async Task Forcing_a_password_change_calls_through()
    {
        var control = new FakeControl();
        var executor = new ForceLocalUserPasswordChangeExecutor(
            control, NullLogger<ForceLocalUserPasswordChangeExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            MakeTask("ForceLocalUserPasswordChange", new { sid = TargetSid, username = "Techsara" }));

        result.Succeeded.ShouldBeTrue();
        control.ForcedChangeSids.ShouldHaveSingleItem().ShouldBe(TargetSid);
    }

    // ------------------------------------------------------- executor names

    [Fact]
    public void Every_executor_task_type_matches_its_server_enum_name()
    {
        // A mismatch here means the dispatcher silently reports "unsupported task type"
        // for a task the server happily queued.
        var control = new FakeControl();
        var secrets = new FakeSecrets();

        new CreateLocalUserExecutor(control, secrets, NullLogger<CreateLocalUserExecutor>.Instance)
            .TaskType.ShouldBe("CreateLocalUser");
        new DeleteLocalUserExecutor(control, NullLogger<DeleteLocalUserExecutor>.Instance)
            .TaskType.ShouldBe("DeleteLocalUser");
        new EnableLocalUserExecutor(control, NullLogger<EnableLocalUserExecutor>.Instance)
            .TaskType.ShouldBe("EnableLocalUser");
        new DisableLocalUserExecutor(control, NullLogger<DisableLocalUserExecutor>.Instance)
            .TaskType.ShouldBe("DisableLocalUser");
        new ResetLocalUserPasswordExecutor(control, secrets, NullLogger<ResetLocalUserPasswordExecutor>.Instance)
            .TaskType.ShouldBe("ResetLocalUserPassword");
        new ForceLocalUserPasswordChangeExecutor(control, NullLogger<ForceLocalUserPasswordChangeExecutor>.Instance)
            .TaskType.ShouldBe("ForceLocalUserPasswordChange");
        new ChangeLocalUserTypeExecutor(control, NullLogger<ChangeLocalUserTypeExecutor>.Instance)
            .TaskType.ShouldBe("ChangeLocalUserType");
        new AddLocalUserToGroupExecutor(control, NullLogger<AddLocalUserToGroupExecutor>.Instance)
            .TaskType.ShouldBe("AddLocalUserToGroup");
        new RemoveLocalUserFromGroupExecutor(control, NullLogger<RemoveLocalUserFromGroupExecutor>.Instance)
            .TaskType.ShouldBe("RemoveLocalUserFromGroup");
    }

    // -------------------------------------------------------------- fakes

    private sealed class FakeControl : ILocalAccountsControl
    {
        public List<(string Username, string Password, bool Enabled, bool MustChange,
            bool Administrator, IReadOnlyList<string> Groups)> CreatedUsers { get; } = [];

        /// <summary>Set to make the fake report a different end state than requested.</summary>
        public CreatedLocalAccount? CreateResult { get; set; }
        public List<string> DeletedSids { get; } = [];
        public List<(string Sid, bool Enabled)> EnabledCalls { get; } = [];
        public List<(string Sid, string Password)> PasswordResets { get; } = [];
        public List<string> ForcedChangeSids { get; } = [];
        public List<(string GroupSid, string MemberSid, bool IsMember)> MembershipCalls { get; } = [];

        public IReadOnlyList<LiveLocalAccount> LiveAccounts { get; set; } = [];

        public ValueTask<CreatedLocalAccount> CreateUserAsync(
            string username, string password, string? fullName, string? description,
            bool enabled, bool mustChangePasswordAtNextLogon, bool administrator,
            IReadOnlyList<string> additionalGroups, CancellationToken cancellationToken = default)
        {
            CreatedUsers.Add((username, password, enabled, mustChangePasswordAtNextLogon,
                administrator, additionalGroups));

            return ValueTask.FromResult(CreateResult ?? new CreatedLocalAccount(
                "S-1-5-21-1-2-3-4242", username, enabled, administrator, additionalGroups,
                SkippedGroups: [], IsInUsersGroup: true));
        }

        public ValueTask DeleteUserAsync(string sid, CancellationToken cancellationToken = default)
        {
            DeletedSids.Add(sid);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetUserEnabledAsync(string sid, bool enabled, CancellationToken cancellationToken = default)
        {
            EnabledCalls.Add((sid, enabled));
            return ValueTask.CompletedTask;
        }

        public ValueTask SetPasswordAsync(string sid, string password, CancellationToken cancellationToken = default)
        {
            PasswordResets.Add((sid, password));
            return ValueTask.CompletedTask;
        }

        public ValueTask ForcePasswordChangeAsync(string sid, CancellationToken cancellationToken = default)
        {
            ForcedChangeSids.Add(sid);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetGroupMembershipAsync(
            string groupSid, string memberSid, bool isMember, CancellationToken cancellationToken = default)
        {
            MembershipCalls.Add((groupSid, memberSid, isMember));
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<LiveLocalAccount>> GetLiveAccountsAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(LiveAccounts);
    }

    private sealed class FakeSecrets : ISecretRedeemer
    {
        public string? Secret { get; set; } = "unused";

        public Task<string?> RedeemAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Secret);
    }
}
