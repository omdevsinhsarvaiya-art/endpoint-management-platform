using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// Profiles are templates, and the group allow-list is a boundary. These assert the
/// boundary holds regardless of what a profile or an operator asks for.
/// </summary>
public sealed class UserConfigurationProfileTests
{
    [Fact]
    public void The_standard_employee_baseline_grants_no_administrator_rights()
    {
        var profile = UserConfigurationProfiles.Find(UserConfigurationProfiles.StandardEmployee);

        profile.ShouldNotBeNull();
        profile!.AccountType.ShouldBe(LocalAccountType.StandardUser);
        profile.GrantsAdministrator.ShouldBeFalse();
        profile.AdditionalGroups.ShouldBeEmpty();
        profile.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void The_it_administrator_baseline_asks_for_administrator_rights()
    {
        var profile = UserConfigurationProfiles.Find(UserConfigurationProfiles.ItAdministrator);

        profile.ShouldNotBeNull();
        profile!.AccountType.ShouldBe(LocalAccountType.Administrator);
        profile.GrantsAdministrator.ShouldBeTrue();
    }

    [Fact]
    public void No_baseline_depends_on_a_group_that_some_windows_editions_lack()
    {
        // Regression: the IT Administrator baseline defaulted to "Remote Desktop
        // Users", which does not exist on Home editions, so creating an IT
        // administrator failed on those machines over a group that was never the
        // point of the request. A baseline must apply on every SKU.
        string[] notOnEverySku = ["Remote Desktop Users", "Backup Operators"];

        foreach (var profile in UserConfigurationProfiles.All.Values)
        {
            foreach (var group in profile.AdditionalGroups)
            {
                notOnEverySku.ShouldNotContain(
                    group,
                    StringComparer.OrdinalIgnoreCase,
                    $"profile '{profile.Key}' must not require a group that some Windows editions lack");
            }
        }
    }

    [Fact]
    public void Administrator_rights_never_come_from_the_additional_group_allow_list()
    {
        // The allow-list is for extras. Administrator is an account type, gated by
        // user.change_type and the last-administrator rules; if it could also be
        // reached as a "group", those gates would have a way around them.
        UserConfigurationProfiles.PermittedAdditionalGroups
            .ShouldNotContain("Administrators", StringComparer.OrdinalIgnoreCase);

        foreach (var profile in UserConfigurationProfiles.All.Values)
        {
            profile.AdditionalGroups.ShouldNotContain(
                "Administrators",
                StringComparer.OrdinalIgnoreCase,
                $"profile '{profile.Key}' must grant administrator via AccountType, not a group");

            // The administrator baseline must still really be an administrator.
            if (profile.Key == UserConfigurationProfiles.ItAdministrator)
            {
                profile.GrantsAdministrator.ShouldBeTrue();
            }
        }
    }

    // ------------------------------------------- allow-list ∩ device groups

    [Fact]
    public void Only_groups_the_device_actually_has_are_offered()
    {
        // What a machine has varies by SKU, so the policy ceiling is intersected with
        // the device's reported groups before anything is offered to an operator.
        var offered = UserConfigurationProfiles.PermittedGroupsPresentOn(
            ["Users", "Performance Log Users", "Guests", "IIS_IUSRS"]);

        offered.ShouldContain("Users");
        offered.ShouldContain("Performance Log Users");

        // Present on the device but not permitted by policy.
        offered.ShouldNotContain("Guests");
        offered.ShouldNotContain("IIS_IUSRS");

        // Permitted by policy but absent from this device.
        offered.ShouldNotContain("Remote Desktop Users");
        offered.ShouldNotContain("Backup Operators");
    }

    [Fact]
    public void The_intersection_matches_device_group_names_case_insensitively()
    {
        // Windows group names are not case-sensitive; a casing difference in reported
        // inventory must not make a group look absent.
        UserConfigurationProfiles.PermittedGroupsPresentOn(["users", "REMOTE DESKTOP USERS"])
            .ShouldBe(["Remote Desktop Users", "Users"], ignoreOrder: true);
    }

    [Fact]
    public void A_device_that_has_never_reported_groups_is_offered_the_full_allow_list()
    {
        // No inventory is missing knowledge, not evidence of a machine with no groups.
        // Offering nothing would be its own wrong answer; the device applies what it
        // has and reports what it skipped.
        UserConfigurationProfiles.PermittedGroupsPresentOn(null)
            .ShouldBe(UserConfigurationProfiles.PermittedAdditionalGroups.Order(StringComparer.OrdinalIgnoreCase));

        UserConfigurationProfiles.PermittedGroupsPresentOn([])
            .Count.ShouldBe(UserConfigurationProfiles.PermittedAdditionalGroups.Count);
    }

    [Fact]
    public void The_intersection_never_offers_a_protected_group_even_if_the_device_has_it()
    {
        // Every machine has an Administrators group; that must not make it offerable.
        UserConfigurationProfiles
            .PermittedGroupsPresentOn(["Administrators", "Power Users", "Hyper-V Administrators", "Users"])
            .ShouldBe(["Users"]);
    }

    [Fact]
    public void An_unknown_profile_resolves_to_null_rather_than_a_default()
    {
        // Silently substituting a default would create an account with settings
        // nobody chose.
        UserConfigurationProfiles.Find("does_not_exist").ShouldBeNull();
        UserConfigurationProfiles.Find(null).ShouldBeNull();
    }

    [Fact]
    public void Administrators_cannot_be_requested_as_an_additional_group()
    {
        // Otherwise "additional groups" becomes a way around the change-type
        // permission and the last-administrator safeguards.
        var refusal = UserConfigurationProfiles.ValidateAdditionalGroup("Administrators");

        refusal.ShouldNotBeNull();
        refusal!.ShouldContain("account type");
    }

    [Theory]
    [InlineData("Power Users")]
    [InlineData("Distributed COM Users")]
    [InlineData("Hyper-V Administrators")]
    [InlineData("Cryptographic Operators")]
    public void Protected_groups_are_refused(string group) =>
        UserConfigurationProfiles.ValidateAdditionalGroup(group).ShouldNotBeNull();

    [Theory]
    [InlineData("Users")]
    [InlineData("Remote Desktop Users")]
    [InlineData("remote desktop users")]
    public void Permitted_groups_are_allowed_case_insensitively(string group) =>
        UserConfigurationProfiles.ValidateAdditionalGroup(group).ShouldBeNull();

    [Fact]
    public void An_unlisted_group_is_refused_rather_than_allowed_by_default()
    {
        // Allow-list, not deny-list: a group added to Windows later is unreachable
        // until someone reviews it.
        UserConfigurationProfiles.ValidateAdditionalGroup("Some Future Group").ShouldNotBeNull();
    }

    [Fact]
    public void A_blank_group_is_refused() =>
        UserConfigurationProfiles.ValidateAdditionalGroup("   ").ShouldNotBeNull();

    [Fact]
    public void No_profile_smuggles_a_protected_group_through_its_defaults()
    {
        // A baseline is still subject to the allow-list.
        foreach (var profile in UserConfigurationProfiles.All.Values)
        {
            foreach (var group in profile.AdditionalGroups)
            {
                UserConfigurationProfiles.ValidateAdditionalGroup(group)
                    .ShouldBeNull($"profile '{profile.Key}' must not default to a forbidden group");
            }
        }
    }
}
