using System.Text.RegularExpressions;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The removal task's catalogue entry: which permission it needs, that it is
/// confirmed and audited as high-risk, and the agent version gate.
/// </summary>
/// <remarks>
/// The gate is the difference between an operator seeing "your agent is too
/// old" and seeing a failed task that reads exactly like an application that
/// would not uninstall.
/// </remarks>
public sealed class RemoveApplicationTaskCatalogTests
{
    private static DeviceTaskDefinition Remove =>
        DeviceTaskCatalog.Require(DeviceTaskType.RemoveApplication);

    /// <summary>The agent parses the type name from the wire; the number is stored as text.</summary>
    [Fact]
    public void The_task_type_is_the_name_the_agent_executor_declares()
    {
        DeviceTaskType.RemoveApplication.ToString().ShouldBe("RemoveApplication");
        ((int)DeviceTaskType.RemoveApplication).ShouldBe(24);
    }

    /// <summary>
    /// Software.Deploy, not Task.Execute: removing installed software is a change
    /// to what a machine runs, which is the decision that permission already
    /// entrusts to a role.
    /// </summary>
    [Fact]
    public void Removal_requires_the_software_deploy_permission_and_is_high_risk()
    {
        Remove.RequiredPermission.ShouldBe(Permissions.Software.Deploy);
        Remove.HighRisk.ShouldBeTrue();
    }

    /// <summary>
    /// The TTL is measured from queue time and <c>TaskExpirySweeper</c> expires
    /// Delivered tasks as well as Queued ones, so it has to cover the wait for a
    /// claim <em>plus</em> the removal. The removal alone is long: the package
    /// path waits up to ten minutes for the deployment engine, and an MSI
    /// uninstall is unbounded because the product's custom actions run inside it.
    /// At half an hour a late-claimed task is expired mid-uninstall and the
    /// agent's genuine result is rejected.
    /// </summary>
    [Fact]
    public void Removal_stays_claimable_for_an_hour_because_an_uninstall_can_outlast_a_short_ttl()
    {
        Remove.DefaultTimeToLiveSeconds.ShouldBe(3600);
    }

    /// <summary>
    /// Deploying the agent and removing an application are the same shape of
    /// problem -- a long, unbounded installer run on a machine that may claim the
    /// task late -- so they carry the same budget, and drifting apart would mean
    /// one of the two reasons had stopped being true.
    /// </summary>
    [Fact]
    public void Removal_carries_the_same_budget_as_the_other_long_installer_task()
    {
        Remove.DefaultTimeToLiveSeconds
            .ShouldBe(DeviceTaskCatalog.Require(DeviceTaskType.UpdateAgent).DefaultTimeToLiveSeconds);
    }

    /// <summary>
    /// The reason for the hour must stay written down next to the number: the
    /// obvious edit is to "tidy" it back to an interactive TTL like its
    /// neighbours, and the comment is what stops that.
    /// </summary>
    [Fact]
    public void The_reason_for_the_longer_budget_is_documented_beside_the_catalog_entry()
    {
        var source = CatalogSource();
        var entry = source.IndexOf("DeviceTaskType.RemoveApplication, Permissions.Software.Deploy", StringComparison.Ordinal);
        entry.ShouldBeGreaterThan(-1);

        // The comment block immediately above the entry, back to the blank line
        // that separates it from StopApplication's.
        var comment = source[..entry];
        comment = comment[(comment.LastIndexOf("\n\n", StringComparison.Ordinal) + 1)..];

        comment.ShouldContain("TaskExpirySweeper");
        comment.ShouldContain("Delivered");
        comment.ShouldContain("queue time");
    }

    [Fact]
    public void Removal_declares_the_first_agent_with_the_executor_as_its_minimum()
    {
        Remove.MinimumAgentVersion.ShouldBe("1.10.0");
    }

    /// <summary>
    /// The console decides whether to offer Remove from its own copy of this
    /// number, <c>MINIMUM_REMOVE_AGENT_VERSION</c> in
    /// <c>dashboard/src/pages/softwareView.ts</c>. One decision, written twice.
    /// </summary>
    /// <remarks>
    /// The literal above pins this side and the console's suite pins that one,
    /// which ties neither to the other. Raise this minimum for a rewritten
    /// executor and only this project fails; its literal is updated, both suites
    /// go green, and the console goes on offering Remove on agents the server now
    /// refuses -- the operator confirms a destructive action and only then reads
    /// NotEligible, which is the defect the gate was added to prevent. Reading the
    /// console's source is what stops either side moving alone. Its twin lives in
    /// <c>dashboard/tests/removeAgentVersionGate.test.ts</c>, so the mismatch fails
    /// whichever suite the author of the change runs.
    /// </remarks>
    [Fact]
    public void The_console_offers_removal_from_the_same_minimum_this_catalog_enforces()
    {
        const string consoleFile = "dashboard/src/pages/softwareView.ts";

        var declaration = Regex.Match(
            RepositorySource("dashboard", "src", "pages", "softwareView.ts"),
            """^export const MINIMUM_REMOVE_AGENT_VERSION = '(?<version>[^']+)'""",
            RegexOptions.Multiline);

        declaration.Success.ShouldBeTrue(
            $"MINIMUM_REMOVE_AGENT_VERSION was not found in {consoleFile}. It is the console's "
            + "half of the agent version gate; if it was renamed or moved, point this test at it "
            + "rather than deleting the check.");

        var consoleMinimum = declaration.Groups["version"].Value;
        consoleMinimum.ShouldBe(
            Remove.MinimumAgentVersion,
            $"The console offers Remove from {consoleMinimum} but this catalogue refuses it below "
            + $"{Remove.MinimumAgentVersion}. These are one decision in two files -- "
            + "MinimumAgentVersion on the RemoveApplication entry of "
            + $"server/Domain/Tasks/DeviceTaskCatalog.cs, and MINIMUM_REMOVE_AGENT_VERSION in "
            + $"{consoleFile}, plus the literal each suite pins -- and both must move together. "
            + "Apart, the console offers Remove, takes the operator confirmation for a destructive "
            + "action, and this server then refuses the task as NotEligible.");
    }

    [Theory]
    [InlineData("1.10.0")]
    [InlineData("1.10.1")]
    [InlineData("1.11.0")]
    [InlineData("2.0.0")]
    [InlineData("1.10.0-beta.1")]
    public void An_agent_at_or_above_the_minimum_is_supported(string agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Remove, agentVersion).ShouldBeTrue();
    }

    /// <summary>
    /// 1.9.0 discovers identity but cannot act on it; a numeric comparison must
    /// not mistake "1.9" for something above "1.10".
    /// </summary>
    [Theory]
    [InlineData("1.9.0")]
    [InlineData("1.9.9")]
    [InlineData("1.7.0")]
    [InlineData("1.6.0")]
    [InlineData(null)]
    public void An_agent_below_the_minimum_or_unknown_is_not_supported(string? agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Remove, agentVersion).ShouldBeFalse();
    }

    /// <summary>Adding the type must not have changed its neighbours' gates.</summary>
    [Fact]
    public void Force_stop_keeps_its_own_minimum()
    {
        DeviceTaskCatalog.Require(DeviceTaskType.StopApplication).MinimumAgentVersion.ShouldBe("1.6.0");
        DeviceTaskCatalog.Require(DeviceTaskType.InstallPackage).MinimumAgentVersion.ShouldBeNull();
    }

    /// <summary>The catalogue's own source.</summary>
    private static string CatalogSource() =>
        RepositorySource("server", "Domain", "Tasks", "DeviceTaskCatalog.cs");

    /// <summary>
    /// Reads one of the repository's source files, with line endings normalised.
    /// </summary>
    /// <remarks>
    /// Two rules here are about text and are not observable any other way: one is
    /// about a comment, and the other is about a constant in a TypeScript file no
    /// .NET assembly can reference. C# files are checked out with CRLF (see
    /// <c>.gitattributes</c>) and TypeScript with LF, so this normalises first
    /// rather than quietly matching nothing.
    /// </remarks>
    private static string RepositorySource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "server", "Domain")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("The repository root was not found above the test output directory.");
        var path = Path.Combine([directory!.FullName, .. pathSegments]);
        File.Exists(path).ShouldBeTrue(path + " was not found.");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
