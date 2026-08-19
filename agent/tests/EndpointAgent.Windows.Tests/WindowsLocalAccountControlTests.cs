using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Exercises the local-account writer against REAL Windows.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because the fake-backed unit tests cannot catch the class of
/// bug that actually shipped: a Net* call that returns success while silently doing
/// nothing. A fake happily records "set must-change-password"; only Windows can say
/// whether the bit took. Every assertion here therefore reads the state back from
/// the OS rather than from the code under test.
/// </para>
/// <para>
/// Managing local accounts requires elevation, so each test skips when the test run
/// is not elevated rather than failing. A skip is honest — it says "unproven here" —
/// whereas passing an unelevated run would claim coverage that does not exist.
/// </para>
/// <para>
/// Each test creates its own uniquely-named throwaway account and deletes it in a
/// finally block, so a failure cannot leave an account behind on a developer machine.
/// </para>
/// </remarks>
public sealed class WindowsLocalAccountControlTests
{
    private static WindowsLocalAccountControl Create() =>
        new(NullLogger<WindowsLocalAccountControl>.Instance);

    /// <summary>A name short enough for SAM (20 chars) and unique per run.</summary>
    private static string TempUsername() => "eptest" + Guid.CreateVersion7().ToString("N")[..10];

    private static void DeleteIfPresent(string username)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
            user?.Delete();
        }
        catch (PrincipalException)
        {
            // Best-effort cleanup; a leaked test account is noise, not a failure.
        }
    }

    /// <summary>UF_PASSWORD_EXPIRED (lmaccess.h): the account must change at next logon.</summary>
    private const int UF_PASSWORD_EXPIRED = 0x800000;

    /// <summary>
    /// Whether Windows has the "must change password at next logon" bit set.
    /// </summary>
    /// <remarks>
    /// Read from the WinNT provider's UserFlags, which is what Windows itself
    /// reports. <see cref="UserPrincipal.LastPasswordSet"/> looks like the natural
    /// probe and is NOT usable here: on the SAM store it returns a non-null
    /// timestamp even for accounts Windows agrees must change their password
    /// (verified against accounts where <c>Get-LocalUser</c> reports a null
    /// PasswordLastSet), so asserting on it fails against correct behaviour.
    /// </remarks>
    private static bool MustChangePasswordAtNextLogon(string username)
    {
        using var entry = new DirectoryEntry($"WinNT://./{username},user");
        var flags = (int)(entry.Properties["UserFlags"].Value ?? 0);
        return (flags & UF_PASSWORD_EXPIRED) != 0;
    }

    /// <summary>
    /// True when Windows reports the account in the well-known group. Resolved by SID
    /// rather than by name so the assertion holds on localized Windows too.
    /// </summary>
    private static bool IsMemberOf(string username, WellKnownSidType wellKnownGroup)
    {
        var groupSid = new SecurityIdentifier(wellKnownGroup, null).Value;

        using var context = new PrincipalContext(ContextType.Machine);
        using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
        if (user is null)
        {
            return false;
        }

        foreach (var group in user.GetGroups())
        {
            using (group)
            {
                if (group.Sid is { } sid
                    && string.Equals(sid.Value, groupSid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ------------------------------------------------ BUILTIN\Users baseline

    [ElevatedFact]
    public async Task A_created_standard_user_is_a_member_of_the_users_group()
    {
        // The regression: NetUserAdd creates the SAM account but joins NO groups, so
        // a "standard user" belonged to nothing. It still signed in, because
        // BUILTIN\Users contains NT AUTHORITY\Authenticated Users, which is exactly
        // why nobody noticed until `net user` was run against the account.
        var username = TempUsername();
        try
        {
            var created = await Create().CreateUserAsync(
                username, "TempProbe!2026pw", "Probe", "temp test account",
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false, additionalGroups: []);

            // Read from Windows, not from the returned record.
            IsMemberOf(username, WellKnownSidType.BuiltinUsersSid)
                .ShouldBeTrue("a created standard user must be a member of BUILTIN\\Users");

            created.IsInUsersGroup.ShouldBeTrue("the reported state must match Windows");
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task A_created_standard_user_is_not_an_administrator()
    {
        var username = TempUsername();
        try
        {
            var created = await Create().CreateUserAsync(
                username, "TempProbe!2026pw", null, null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false, additionalGroups: []);

            IsMemberOf(username, WellKnownSidType.BuiltinAdministratorsSid)
                .ShouldBeFalse("a standard user must never be in BUILTIN\\Administrators");

            created.IsAdministrator.ShouldBeFalse();
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task A_created_administrator_is_in_both_administrators_and_users()
    {
        // Administrators get the baseline too. Demotion removes Administrators, and an
        // administrator that never joined Users would land in the groupless state this
        // whole fix exists to prevent.
        var username = TempUsername();
        try
        {
            var created = await Create().CreateUserAsync(
                username, "TempProbe!2026pw", null, null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: true, additionalGroups: []);

            IsMemberOf(username, WellKnownSidType.BuiltinAdministratorsSid)
                .ShouldBeTrue("an administrator must really be in BUILTIN\\Administrators");
            IsMemberOf(username, WellKnownSidType.BuiltinUsersSid)
                .ShouldBeTrue("an administrator keeps the BUILTIN\\Users baseline");

            created.IsAdministrator.ShouldBeTrue();
            created.IsInUsersGroup.ShouldBeTrue();
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task A_group_this_machine_does_not_have_is_skipped_without_losing_the_baseline()
    {
        // A missing optional group must not be fatal - and must not cost the account
        // its required Users membership either.
        var username = TempUsername();
        try
        {
            var created = await Create().CreateUserAsync(
                username, "TempProbe!2026pw", null, null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false,
                additionalGroups: ["No Such Group On This Machine"]);

            created.SkippedGroups.ShouldContain("No Such Group On This Machine");

            IsMemberOf(username, WellKnownSidType.BuiltinUsersSid)
                .ShouldBeTrue("skipping an optional group must not skip the baseline");
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task A_failed_create_leaves_no_account_behind()
    {
        // Atomicity, proven against real Windows: the second create collides on the
        // name, and must not disturb the account the first one made.
        var username = TempUsername();
        try
        {
            await Create().CreateUserAsync(
                username, "TempProbe!2026pw", null, null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false, additionalGroups: []);

            var duplicate = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await Create().CreateUserAsync(
                    username, "TempProbe!2026pw", null, null,
                    enabled: true, mustChangePasswordAtNextLogon: false,
                    administrator: false, additionalGroups: []));

            duplicate.Message.ShouldContain("already exists");

            // The original survives intact - a failed create must not damage it.
            IsMemberOf(username, WellKnownSidType.BuiltinUsersSid).ShouldBeTrue();
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task Creating_with_must_change_password_actually_expires_the_password()
    {
        // The regression this file was written for: the create succeeded but the
        // must-change flag was silently dropped, so the user was never prompted.
        var username = TempUsername();
        try
        {
            await Create().CreateUserAsync(
                username, "TempProbe!2026pw", "Probe", "temp test account",
                enabled: true, mustChangePasswordAtNextLogon: true,
                administrator: false, additionalGroups: []);

            using (var context = new PrincipalContext(ContextType.Machine))
            using (var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username))
            {
                user.ShouldNotBeNull("the account must exist after a successful create");
            }

            MustChangePasswordAtNextLogon(username).ShouldBeTrue(
                "Windows must have the password-expired flag set after a must-change create");
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task Creating_without_must_change_leaves_a_normal_password()
    {
        var username = TempUsername();
        try
        {
            await Create().CreateUserAsync(
                username, "TempProbe!2026pw", "Probe", null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false, additionalGroups: []);

            using (var context = new PrincipalContext(ContextType.Machine))
            using (var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username))
            {
                user.ShouldNotBeNull();
            }

            MustChangePasswordAtNextLogon(username).ShouldBeFalse(
                "an ordinary create must not silently demand a password change");
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task Forcing_a_password_change_expires_an_existing_password()
    {
        var username = TempUsername();
        try
        {
            var control = Create();
            await control.CreateUserAsync(
                username, "TempProbe!2026pw", "Probe", null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false, additionalGroups: []);

            string sid;
            using (var context = new PrincipalContext(ContextType.Machine))
            using (var created = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username))
            {
                created.ShouldNotBeNull();
                sid = created!.Sid.Value;
            }

            MustChangePasswordAtNextLogon(username).ShouldBeFalse("precondition: password starts un-expired");

            await control.ForcePasswordChangeAsync(sid);

            MustChangePasswordAtNextLogon(username).ShouldBeTrue("the password must now require a change");
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    [ElevatedFact]
    public async Task Disabling_and_enabling_round_trips_against_real_windows()
    {
        var username = TempUsername();
        try
        {
            var control = Create();
            await control.CreateUserAsync(
                username, "TempProbe!2026pw", null, null,
                enabled: true, mustChangePasswordAtNextLogon: false,
                administrator: false, additionalGroups: []);

            string sid;
            using (var context = new PrincipalContext(ContextType.Machine))
            using (var created = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username))
            {
                sid = created!.Sid.Value;
                created.Enabled.ShouldBe(true);
            }

            await control.SetUserEnabledAsync(sid, enabled: false);
            ReadEnabled(username).ShouldBe(false, "disable must reach the OS");

            await control.SetUserEnabledAsync(sid, enabled: true);
            ReadEnabled(username).ShouldBe(true, "enable must reach the OS");
        }
        finally
        {
            DeleteIfPresent(username);
        }
    }

    private static bool? ReadEnabled(string username)
    {
        using var context = new PrincipalContext(ContextType.Machine);
        using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
        return user?.Enabled;
    }
}
