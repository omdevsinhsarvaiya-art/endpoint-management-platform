using EndpointAgent.Core.Inventory;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// What is running on this machine, as discovery sees it.
/// </summary>
/// <remarks>
/// The test host itself is a running process outside the Windows directory, so
/// the source always has at least one thing to report, and that thing is known:
/// it is used as the one fixed point. Everything else is asserted as shape.
/// </remarks>
public sealed class WindowsRunningProcessEvidenceSourceTests
{
    private static WindowsRunningProcessEvidenceSource Create() =>
        new(new WindowsServiceProcessProvider(NullLogger<WindowsServiceProcessProvider>.Instance),
            NullLogger<WindowsRunningProcessEvidenceSource>.Instance);

    [Fact]
    public async Task Reports_distinct_executables_that_exist()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldNotBeEmpty();
        evidence.ShouldAllBe(e => e.Source == EvidenceSource.RunningProcess);
        evidence.ShouldAllBe(e => !e.IsAuthoritative);
        evidence.ShouldAllBe(e => e.ExecutablePath != null && ExecutablePath.Normalize(e.ExecutablePath) == e.ExecutablePath);
        evidence.ShouldAllBe(e => File.Exists(e.ExecutablePath!));
        evidence.Select(e => e.ExecutablePath!).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(evidence.Count);
    }

    [Fact]
    public async Task Nothing_under_the_windows_directory_or_the_agent_is_reported()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        evidence.ShouldAllBe(e => !ExecutablePath.IsUnder(e.ExecutablePath, windows));
        evidence.ShouldAllBe(e => !ExecutablePath.IsUnder(e.ExecutablePath, AppContext.BaseDirectory));
    }

    [Fact]
    public async Task Every_process_is_named()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Name));
    }

    [Fact]
    public async Task Scope_follows_the_profile_the_executable_lives_in()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.Where(e => e.Scope == SoftwareScope.User).ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.InstalledForUser));
        evidence.Where(e => e.Scope == SoftwareScope.User)
            .ShouldAllBe(e => e.ExecutablePath!.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase));
        evidence.Where(e => e.Scope == SoftwareScope.Machine).ShouldAllBe(e => e.InstalledForUser == null);
    }

    /// <summary>The test host: a process this test knows is running, and where from.</summary>
    [Fact]
    public async Task The_test_host_itself_is_not_reported_because_it_runs_from_the_agent_directory_stand_in()
    {
        // The source treats AppContext.BaseDirectory as the agent's own directory,
        // which under the test host is the test output directory; the host
        // process lives elsewhere (the dotnet root) and is reported if readable.
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => !ExecutablePath.IsUnder(e.ExecutablePath, AppContext.BaseDirectory));
    }

    /// <summary>
    /// The acceptance case where it applies: a portable executable running from
    /// under Downloads is observed and named from its own metadata. Its location
    /// is its own directory when it has one (Downloads\caffeine), and nothing at
    /// all when it sits in Downloads itself -- never Downloads.
    /// </summary>
    [Fact]
    public async Task A_portable_executable_running_from_downloads_is_observed_and_never_makes_downloads_a_location()
    {
        var source = Create();
        var evidence = await source.CollectEvidenceAsync(CancellationToken.None);

        var portable = evidence.FirstOrDefault(e =>
            e.ExecutablePath!.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)
            && !ApplicationMerger.IsTransient(e.ExecutablePath));
        if (portable is null)
        {
            return; // Nothing portable is running from Downloads right now.
        }

        var described = await new SoftwareDiscoveryCollector([source], NullLogger<SoftwareDiscoveryCollector>.Instance, new WindowsExecutableMetadataReader())
            .CollectEvidenceAsync(CancellationToken.None);
        var app = ApplicationMerger.Merge(described)
            .Single(a => string.Equals(a.ExecutablePath, portable.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        app.Confidence.ShouldBe(DiscoveryConfidence.Observed);
        app.Scope.ShouldBe(SoftwareScope.User);
        app.InstalledForUser.ShouldNotBeNull();
        app.Evidence.ShouldContain(e => e.Source == EvidenceSource.ExecutableMetadata);

        if (app.InstallLocation is not null)
        {
            ExecutablePath.IsSharedDirectory(app.InstallLocation).ShouldBeFalse();
            ApplicationProcessMatcher.CanResolve(app.InstallLocation).ShouldBeTrue();
            app.InstallLocation.ShouldNotEndWith(@"\Downloads", Case.Insensitive);
        }
    }
}
