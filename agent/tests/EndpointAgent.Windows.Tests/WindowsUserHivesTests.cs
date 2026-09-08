using System.Security.Principal;
using EndpointAgent.Windows;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The loaded-hive helper every per-user source shares, against this machine.
/// </summary>
public sealed class WindowsUserHivesTests
{
    private static string CurrentSid() => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public void The_current_user_is_among_the_loaded_hives()
    {
        var hives = WindowsUserHives.Loaded();

        hives.ShouldContain(h => h.Sid == CurrentSid());
        hives.ShouldAllBe(h => WindowsUserHives.IsRealUserSid(h.Sid));
        hives.ShouldAllBe(h => !string.IsNullOrWhiteSpace(h.Account));
    }

    [Theory]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-19", false)]
    [InlineData("S-1-5-20", false)]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-21-1-2-3-1001_Classes", false)]
    [InlineData("S-1-12-1-1-2-3-4", true)]
    [InlineData(".DEFAULT", false)]
    public void Only_people_are_real_user_sids(string sid, bool real)
    {
        WindowsUserHives.IsRealUserSid(sid).ShouldBe(real);
    }

    [Fact]
    public void The_current_users_profile_path_is_a_real_directory()
    {
        var profile = WindowsUserHives.ProfilePath(CurrentSid()).ShouldNotBeNull();

        Directory.Exists(profile).ShouldBeTrue(profile);
        Path.IsPathFullyQualified(profile).ShouldBeTrue();
    }

    [Fact]
    public void The_current_users_start_menu_is_a_local_absolute_directory()
    {
        var programs = WindowsUserHives.StartMenuPrograms(CurrentSid()).ShouldNotBeNull();

        Path.IsPathFullyQualified(programs).ShouldBeTrue();
        programs.ShouldNotStartWith(@"\\");
        programs.ShouldEndWith(@"\Start Menu\Programs", Case.Insensitive);
    }

    [Theory]
    [InlineData("S-1-5-21-0-0-0-424242")]
    [InlineData("")]
    [InlineData(@"S-1-5-21-1-2-3-1001\..\..\SOFTWARE")]
    public void An_unknown_or_malformed_sid_has_no_profile(string sid)
    {
        WindowsUserHives.ProfilePath(sid).ShouldBeNull();
        WindowsUserHives.StartMenuPrograms(sid).ShouldBeNull();
    }
}
