using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// The guards that stop a local-account change from locking the organization out of
/// a device. These are the rules the API pre-checks and the agent re-checks live.
/// </summary>
public sealed class LocalAccountSafetyRulesTests
{
    private const string BuiltInAdmin = "S-1-5-21-1111111111-2222222222-3333333333-500";
    private const string AdminA = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string AdminB = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Standard = "S-1-5-21-1111111111-2222222222-3333333333-1003";

    private static LocalAccountView Account(string sid, bool enabled, bool admin, string name = "user") =>
        new(sid, name, enabled, admin);

    [Fact]
    public void The_built_in_administrator_cannot_be_deleted()
    {
        var users = new[] { Account(BuiltInAdmin, true, true), Account(AdminA, true, true) };

        LocalAccountSafetyRules.ValidateDelete(BuiltInAdmin, users)
            .ShouldNotBeNull("RID 500 is protected even when other admins exist");
    }

    [Fact]
    public void The_built_in_administrator_cannot_be_disabled()
    {
        var users = new[] { Account(BuiltInAdmin, true, true), Account(AdminA, true, true) };

        LocalAccountSafetyRules.ValidateDisable(BuiltInAdmin, users).ShouldNotBeNull();
    }

    [Fact]
    public void Deleting_the_last_enabled_administrator_is_refused()
    {
        var users = new[] { Account(AdminA, true, true), Account(Standard, true, false) };

        var refusal = LocalAccountSafetyRules.ValidateDelete(AdminA, users);

        refusal.ShouldNotBeNull();
        refusal!.ShouldContain("no enabled administrator");
    }

    [Fact]
    public void Disabling_the_last_enabled_administrator_is_refused()
    {
        var users = new[] { Account(AdminA, true, true), Account(Standard, true, false) };

        LocalAccountSafetyRules.ValidateDisable(AdminA, users).ShouldNotBeNull();
    }

    [Fact]
    public void Demoting_the_last_enabled_administrator_is_refused()
    {
        var users = new[] { Account(AdminA, true, true), Account(Standard, true, false) };

        LocalAccountSafetyRules.ValidateDemote(AdminA, users).ShouldNotBeNull();
    }

    [Fact]
    public void A_disabled_administrator_does_not_count_as_the_safety_net()
    {
        // AdminB exists but is disabled, so removing AdminA still strands the device.
        var users = new[] { Account(AdminA, true, true), Account(AdminB, false, true) };

        LocalAccountSafetyRules.ValidateDemote(AdminA, users)
            .ShouldNotBeNull("a disabled administrator cannot recover the machine");
    }

    [Fact]
    public void Demoting_one_of_two_enabled_administrators_is_allowed()
    {
        var users = new[] { Account(AdminA, true, true), Account(AdminB, true, true) };

        LocalAccountSafetyRules.ValidateDemote(AdminA, users).ShouldBeNull();
    }

    [Fact]
    public void Deleting_a_standard_user_is_always_allowed_by_the_admin_rule()
    {
        var users = new[] { Account(AdminA, true, true), Account(Standard, true, false) };

        LocalAccountSafetyRules.ValidateDelete(Standard, users).ShouldBeNull();
    }

    [Fact]
    public void An_unknown_target_defers_to_the_agents_live_check()
    {
        // Inventory may predate the account; refusing here would block legitimate work,
        // and the agent re-checks against live Windows state anyway.
        var users = new[] { Account(AdminA, true, true) };

        LocalAccountSafetyRules.ValidateDelete("S-1-5-21-9-9-9-4242", users).ShouldBeNull();
    }

    [Fact]
    public void Disabling_an_already_disabled_administrator_is_allowed()
    {
        var users = new[] { Account(AdminA, true, true), Account(AdminB, false, true) };

        LocalAccountSafetyRules.ValidateDisable(AdminB, users)
            .ShouldBeNull("it is already not counted among the enabled administrators");
    }
}
