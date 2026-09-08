using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// Turning the many shapes an executable path arrives in into one comparable
/// form, or refusing it.
/// </summary>
/// <remarks>
/// Discovery reads paths from a registry default value, a shortcut target and a
/// process image path, and none of those is careful. Two spellings of one path
/// would be two applications; a path that is not a local file is not something
/// discovery should reason about. Nothing here is ever executed, so refusing costs
/// one piece of evidence and accepting wrongly costs a wrong row.
/// </remarks>
public sealed class ExecutablePathTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Slack\slack.exe", @"C:\Program Files\Slack\slack.exe")]
    [InlineData(@"  C:\Program Files\Slack\slack.exe  ", @"C:\Program Files\Slack\slack.exe")]
    [InlineData("\"C:\\Program Files\\Slack\\slack.exe\"", @"C:\Program Files\Slack\slack.exe")]
    [InlineData("\"C:\\Program Files\\Slack\\slack.exe\" --arg=1 \"%1\"", @"C:\Program Files\Slack\slack.exe")]
    [InlineData(@"C:/Program Files/Slack/slack.exe", @"C:\Program Files\Slack\slack.exe")]
    [InlineData(@"C:\Program Files\Slack\slack.exe\", @"C:\Program Files\Slack\slack.exe")]
    public void Accepts_an_absolute_local_path_in_any_of_its_spellings(string raw, string expected)
    {
        ExecutablePath.Normalize(raw).ShouldBe(expected);
    }

    /// <summary>
    /// Only what can be reasoned about: relative paths, shares, device paths and
    /// bare names are refused rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("slack.exe")]
    [InlineData(@"..\slack.exe")]
    [InlineData(@"\\server\share\slack.exe")]
    [InlineData(@"\\?\C:\slack.exe")]
    [InlineData(@"C:slack.exe")]
    [InlineData("\"\"")]
    [InlineData("\"unterminated")]
    public void Refuses_anything_that_is_not_an_absolute_local_path(string? raw)
    {
        ExecutablePath.Normalize(raw).ShouldBeNull();
    }

    /// <summary>
    /// A traversal in a path that was read rather than constructed is a reason to
    /// distrust the value, not to canonicalise it.
    /// </summary>
    [Fact]
    public void Refuses_a_path_with_a_traversal()
    {
        ExecutablePath.Normalize(@"C:\Program Files\..\Windows\System32\cmd.exe").ShouldBeNull();
    }

    [Theory]
    [InlineData("C:\\Program Files\\a\0b.exe")]
    [InlineData("C:\\Program Files\\a\nb.exe")]
    [InlineData(@"C:\Program Files\a*b.exe")]
    [InlineData(@"C:\Program Files\a|b.exe")]
    public void Refuses_control_and_wildcard_characters(string raw)
    {
        ExecutablePath.Normalize(raw).ShouldBeNull();
    }

    [Fact]
    public void Refuses_a_path_that_would_not_fit_on_the_wire()
    {
        ExecutablePath.Normalize(@"C:\" + new string('a', 600) + ".exe").ShouldBeNull();
    }

    [Fact]
    public void Knows_the_directory_of_a_path()
    {
        ExecutablePath.DirectoryOf(@"C:\Program Files\Slack\slack.exe").ShouldBe(@"C:\Program Files\Slack");
        ExecutablePath.DirectoryOf(null).ShouldBeNull();
        ExecutablePath.DirectoryOf(@"C:\").ShouldBeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\Techsara\Downloads", true)]
    [InlineData(@"C:\Users\Techsara\Desktop", true)]
    [InlineData(@"C:\Users\Techsara\Documents\", true)]
    [InlineData(@"c:\users\techsara\downloads", true)]
    [InlineData(@"C:\Users\Techsara", true)]
    [InlineData(@"C:\Users\Public\Downloads", true)]
    [InlineData(@"C:\Users\Techsara\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\Techsara\AppData\Local\Temp\7zS1234", true)]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"C:\", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData(@"C:\Users\Techsara\Downloads\Caffeine", false)]
    [InlineData(@"C:\Users\Techsara\AppData\Local\Programs\antigravity", false)]
    [InlineData(@"C:\Tools\Caffeine", false)]
    [InlineData(@"C:\Program Files\Contoso", false)]
    [InlineData(@"C:\Temporal\app", false)]
    public void Knows_which_directories_are_shared_rather_than_an_applications_own(string? directory, bool shared)
    {
        ExecutablePath.IsSharedDirectory(directory).ShouldBe(shared);
    }

    /// <summary>Containment respects a directory boundary, exactly as the process matcher does.</summary>
    [Fact]
    public void Containment_respects_the_directory_boundary()
    {
        ExecutablePath.IsUnder(@"C:\Program Files\Contoso\app.exe", @"C:\Program Files\Contoso").ShouldBeTrue();
        ExecutablePath.IsUnder(@"C:\Program Files\Contoso\app.exe", @"C:\Program Files\Contoso\").ShouldBeTrue();
        ExecutablePath.IsUnder(@"c:\program files\contoso\app.exe", @"C:\Program Files\Contoso").ShouldBeTrue();
        ExecutablePath.IsUnder(@"C:\Program Files\ContosoExtra\app.exe", @"C:\Program Files\Contoso").ShouldBeFalse();
        ExecutablePath.IsUnder(null, @"C:\Program Files\Contoso").ShouldBeFalse();
        ExecutablePath.IsUnder(@"C:\x\a.exe", null).ShouldBeFalse();
    }
}
