using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests against this machine's real local SAM. Read-only,
/// unelevated. Assertions target invariants every Windows installation satisfies:
/// the built-in Administrator account and Administrators/Users groups exist with
/// their well-known SIDs.
/// </summary>
public sealed class WindowsLocalAccountsCollectorTests
{
    private static WindowsLocalAccountsCollector CreateCollector() =>
        new(NullLogger<WindowsLocalAccountsCollector>.Instance);

    [Fact]
    public async Task Collects_users_and_groups_without_throwing()
    {
        var snapshot = await CreateCollector().CollectAsync(CancellationToken.None);

        snapshot.Users.ShouldNotBeEmpty("every Windows machine has local accounts");
        snapshot.Groups.ShouldNotBeEmpty("every Windows machine has local groups");
    }

    [Fact]
    public async Task Every_user_has_a_structurally_valid_sid_and_a_name()
    {
        var snapshot = await CreateCollector().CollectAsync(CancellationToken.None);

        foreach (var user in snapshot.Users)
        {
            user.Sid.ShouldStartWith("S-1-");
            user.Name.ShouldNotBeNullOrWhiteSpace();
        }

        snapshot.Users.Select(u => u.Sid).ShouldBeUnique();
    }

    [Fact]
    public async Task The_built_in_administrator_account_is_reported()
    {
        var snapshot = await CreateCollector().CollectAsync(CancellationToken.None);

        // RID 500 is the built-in Administrator regardless of rename or locale.
        snapshot.Users.ShouldContain(
            u => u.Sid.EndsWith("-500", StringComparison.Ordinal),
            "the RID-500 built-in Administrator exists on every Windows installation");
    }

    [Fact]
    public async Task The_administrators_group_is_reported_with_its_well_known_sid()
    {
        var snapshot = await CreateCollector().CollectAsync(CancellationToken.None);

        var administrators = snapshot.Groups.SingleOrDefault(g => g.Sid == "S-1-5-32-544");

        administrators.ShouldNotBeNull("BUILTIN\\Administrators is S-1-5-32-544 on every machine");
        administrators.Members.ShouldNotBeEmpty("Administrators always has at least one member");
    }

    [Fact]
    public async Task The_users_group_is_reported()
    {
        var snapshot = await CreateCollector().CollectAsync(CancellationToken.None);

        snapshot.Groups.ShouldContain(g => g.Sid == "S-1-5-32-545"); // BUILTIN\Users
    }

    [Fact]
    public async Task Administrator_flags_agree_with_administrators_group_membership()
    {
        var snapshot = await CreateCollector().CollectAsync(CancellationToken.None);

        var adminMemberSids = snapshot.Groups
            .Single(g => g.Sid == "S-1-5-32-544")
            .Members
            .Where(m => m.Sid is not null)
            .Select(m => m.Sid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var user in snapshot.Users)
        {
            user.IsLocalAdministrator.ShouldBe(
                adminMemberSids.Contains(user.Sid),
                $"flag for {user.Name} must match actual Administrators membership");
        }
    }

    [Fact]
    public void No_string_field_can_carry_credential_material()
    {
        // PasswordRequired/PasswordExpires are policy BOOLEANS and are fine. What
        // must never exist is a string-typed member that could carry a hash or
        // secret; this pins that for future contract changes.
        var stringProperties = typeof(EndpointPlatform.Contracts.Agent.InventoryLocalUser)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name);

        foreach (var name in stringProperties)
        {
            name.ShouldNotContain("Password");
            name.ShouldNotContain("Secret");
            name.ShouldNotContain("Hash");
        }
    }
}
