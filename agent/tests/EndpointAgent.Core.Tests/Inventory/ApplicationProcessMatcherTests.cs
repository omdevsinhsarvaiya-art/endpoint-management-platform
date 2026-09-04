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

/// <summary>
/// The agent must never be able to terminate itself.
/// </summary>
/// <remarks>
/// Recovering an endpoint whose agent has been stopped needs someone physically
/// at the machine, so this is not an ordinary application failure. The guard has
/// to hold for the honest case (the agent is genuinely an installed application
/// and resolves like one) and for a hostile or merely broken install location
/// that resolves onto the agent's directory from either direction.
/// </remarks>
public sealed class ApplicationProcessMatcherSelfProtectionTests
{
    private const string AgentDir = @"C:\Program Files\EndpointPlatform\Agent";

    private static readonly RunningProcess[] Running =
    [
        new(1000, "EndpointAgent.Service.exe", @"C:\Program Files\EndpointPlatform\Agent\EndpointAgent.Service.exe"),
        new(1001, "helper.exe", @"C:\Program Files\EndpointPlatform\Agent\plugins\helper.exe"),
        new(1002, "other.exe", @"C:\Program Files\Contoso\other.exe"),
    ];

    [Fact]
    public void Refuses_the_agents_own_install_directory()
    {
        var matches = ApplicationProcessMatcher.Match(AgentDir, Running, protectedDirectory: AgentDir);

        Assert.Empty(matches);
    }

    /// <remarks>
    /// The case that separates the two guards. Filtering agent processes out of
    /// the result would also satisfy "the agent does not stop itself", but it
    /// would still terminate <c>neighbour.exe</c> on the strength of a root that
    /// was never a single application's install directory. An install location
    /// broad enough to contain the agent is not trustworthy for anything under
    /// it, so the whole request is refused rather than trimmed.
    /// </remarks>
    [Fact]
    public void Refuses_a_parent_of_the_agent_directory_entirely()
    {
        var neighbour = new RunningProcess(
            1004, "neighbour.exe", @"C:\Program Files\EndpointPlatform\Other\neighbour.exe");

        var matches = ApplicationProcessMatcher.Match(
            @"C:\Program Files\EndpointPlatform",
            [.. Running, neighbour],
            protectedDirectory: AgentDir);

        Assert.Empty(matches);
    }

    [Fact]
    public void Refuses_a_subdirectory_of_the_agent_directory()
    {
        var matches = ApplicationProcessMatcher.Match(
            AgentDir + @"\plugins", Running, protectedDirectory: AgentDir);

        Assert.Empty(matches);
    }

    [Fact]
    public void Refuses_regardless_of_trailing_separator_or_casing()
    {
        var matches = ApplicationProcessMatcher.Match(
            @"c:\program files\endpointplatform\agent\", Running, protectedDirectory: AgentDir + "\\");

        Assert.Empty(matches);
    }

    [Fact]
    public void Still_matches_an_unrelated_application()
    {
        var matches = ApplicationProcessMatcher.Match(
            @"C:\Program Files\Contoso", Running, protectedDirectory: AgentDir);

        Assert.Equal([1002], matches.Select(m => m.ProcessId));
    }

    [Fact]
    public void Never_returns_a_process_running_from_the_agent_directory()
    {
        // Defence in depth. Given the containment check above this is currently
        // unreachable -- a process can only be under both the requested root and
        // the agent directory if one contains the other, which is already
        // refused -- and the paths it sees are canonical, since they come from
        // the Win32 module list. It is asserted as a property rather than
        // removed: it is what keeps a future change to either check from
        // silently making the agent stoppable.
        var matches = ApplicationProcessMatcher.Match(
            @"C:\Program Files", Running, protectedDirectory: AgentDir);

        Assert.DoesNotContain(matches, m => m.ProcessId is 1000 or 1001);
    }

    [Fact]
    public void Behaves_as_before_when_no_directory_is_protected()
    {
        var matches = ApplicationProcessMatcher.Match(@"C:\Program Files\Contoso", Running);

        Assert.Equal([1002], matches.Select(m => m.ProcessId));
    }
}
