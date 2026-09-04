using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// Deciding which running processes belong to an installed application.
/// </summary>
/// <remarks>
/// The safety-critical half of Force Stop. Being wrong here means terminating
/// somebody else's process, so these tests are mostly about what the matcher
/// refuses to claim.
/// </remarks>
public sealed class ApplicationProcessMatcherTests
{
    private static RunningProcess P(int pid, string name, string? path) => new(pid, name, path);

    private static readonly RunningProcess[] Machine =
    [
        P(1000, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        P(1001, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        P(2000, "Code", @"C:\Users\techsara\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
        P(3000, "explorer", @"C:\Windows\explorer.exe"),
        P(4000, "svchost", @"C:\Windows\System32\svchost.exe"),
        P(5000, "notepad", null),
    ];

    /// <summary>
    /// The display name and the image name are different things. Chrome installs
    /// as "Google Chrome" and runs as chrome.exe; the match comes from the path,
    /// which is evidence, not from the name, which would be a guess.
    /// </summary>
    [Fact]
    public void An_application_claims_every_process_under_its_install_directory()
    {
        var matches = ApplicationProcessMatcher.Match(
            @"C:\Program Files\Google\Chrome\Application", Machine);

        matches.Select(m => m.ProcessId).ShouldBe([1000, 1001], ignoreOrder: true);
        matches.ShouldAllBe(m => m.ImageName == "chrome");
    }

    /// <summary>
    /// VS Code is the canonical name-mismatch case: "Microsoft Visual Studio
    /// Code" running as Code.exe, from a per-user directory.
    /// </summary>
    [Fact]
    public void A_per_user_install_is_matched_by_its_own_path()
    {
        var matches = ApplicationProcessMatcher.Match(
            @"C:\Users\techsara\AppData\Local\Programs\Microsoft VS Code", Machine);

        matches.Single().ProcessId.ShouldBe(2000);
        matches.Single().ImageName.ShouldBe("Code");
    }

    [Fact]
    public void An_application_with_nothing_running_claims_nothing()
    {
        ApplicationProcessMatcher.Match(@"C:\Program Files\VideoLAN\VLC", Machine).ShouldBeEmpty();
    }

    /// <summary>
    /// A trailing separator is respected, so a directory cannot claim a sibling
    /// whose name merely starts the same way.
    /// </summary>
    [Fact]
    public void A_sibling_directory_with_a_shared_prefix_is_not_claimed()
    {
        var processes = new[]
        {
            P(6000, "app", @"C:\Program Files\Contoso\app.exe"),
            P(6001, "other", @"C:\Program Files\ContosoExtra\other.exe"),
        };

        var matches = ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso", processes);

        matches.Single().ProcessId.ShouldBe(6000);
    }

    /// <summary>
    /// An install location that is a system or shared root would otherwise claim a
    /// large share of everything running, Windows included. Refusing it is what
    /// stops one badly-registered application becoming a request to terminate the
    /// operating system.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\ProgramData")]
    public void An_over_broad_install_location_resolves_to_nothing(string location)
    {
        ApplicationProcessMatcher.Match(location, Machine).ShouldBeEmpty();
        ApplicationProcessMatcher.CanResolve(location).ShouldBeFalse();
    }

    /// <summary>
    /// Windows' own processes are never claimed, because no legitimate
    /// application install location contains them.
    /// </summary>
    [Fact]
    public void System_processes_are_never_matched()
    {
        foreach (var location in new[] { @"C:\Program Files\Google\Chrome\Application", @"C:\Program Files\Contoso" })
        {
            ApplicationProcessMatcher.Match(location, Machine)
                .ShouldAllBe(m => m.ProcessId != 3000 && m.ProcessId != 4000);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"..\relative")]
    [InlineData(@"\\server\share\app")]
    public void An_unusable_install_location_means_no_mapping(string? location)
    {
        ApplicationProcessMatcher.CanResolve(location).ShouldBeFalse();
        ApplicationProcessMatcher.Match(location, Machine).ShouldBeEmpty();
    }

    /// <summary>A process with no reported path is no evidence, so it is skipped.</summary>
    [Fact]
    public void A_process_without_an_executable_path_is_not_matched()
    {
        var processes = new[] { P(7000, "mystery", null) };

        ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso", processes).ShouldBeEmpty();
    }

    /// <summary>PIDs 0 and 4 are System Idle and System, never an application.</summary>
    [Fact]
    public void The_system_pids_are_refused_even_if_a_path_appears_to_match()
    {
        var processes = new[]
        {
            P(4, "System", @"C:\Program Files\Contoso\app.exe"),
            P(0, "Idle", @"C:\Program Files\Contoso\app.exe"),
            P(9000, "app", @"C:\Program Files\Contoso\app.exe"),
        };

        ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso", processes)
            .Select(m => m.ProcessId).ShouldBe([9000]);
    }

    [Fact]
    public void Quotes_and_trailing_separators_do_not_defeat_the_match()
    {
        var processes = new[] { P(8000, "app", @"""C:\Program Files\Contoso\app.exe""") };

        ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso\", processes)
            .Single().ProcessId.ShouldBe(8000);
    }

    [Fact]
    public void Matching_is_case_insensitive_as_Windows_paths_are()
    {
        var processes = new[] { P(8100, "app", @"c:\program files\contoso\APP.EXE") };

        ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso", processes)
            .Single().ProcessId.ShouldBe(8100);
    }

    [Fact]
    public void A_duplicate_process_id_is_reported_once()
    {
        var processes = new[]
        {
            P(8200, "app", @"C:\Program Files\Contoso\app.exe"),
            P(8200, "app", @"C:\Program Files\Contoso\app.exe"),
        };

        ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso", processes).Count.ShouldBe(1);
    }
}
