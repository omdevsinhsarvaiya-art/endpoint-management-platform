using System.Reflection;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Enforces the agent's "no shell execution" rule (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// The agent runs as LocalSystem. The single most dangerous pattern it could adopt
/// is composing a command string and handing it to a shell (cmd/PowerShell),
/// because every string on that path becomes a potential privileged injection.
/// </para>
/// <para>
/// The precise, enforceable guarantees:
/// </para>
/// <list type="number">
///   <item><b>Core stays OS-agnostic:</b> it references no process API at all.</item>
///   <item><b>Nothing calls <c>Process.Start</c>:</b> a source scan over every agent
///   .cs file asserts the shell/launch vector is absent. Reviewed process
///   <em>control</em> - <c>ServiceController</c>, <c>Process.GetProcessById</c>,
///   <c>Process.Kill</c> with an expected-image guard (Phase 9) - is permitted,
///   because it takes typed arguments and has no command line to inject into.</item>
///   <item><b>No PowerShell SDK</b> in either assembly.</item>
/// </list>
/// <para>
/// If a future feature genuinely needs <c>Process.Start</c> (approved-script
/// execution, Phase 10-full), it arrives behind the signed-script pipeline and
/// this scan is tightened to allow exactly that reviewed call site.
/// </para>
/// </remarks>
public sealed class AgentSafetyTests
{
    private static readonly Assembly AgentCore = typeof(EndpointAgent.Core.Configuration.AgentOptions).Assembly;
    private static readonly Assembly AgentWindows = typeof(EndpointAgent.Windows.WindowsSystemInfoProvider).Assembly;

    [Fact]
    public void Core_references_no_process_api_at_all()
    {
        // Core is platform-neutral logic; it must never touch OS process APIs.
        AgentCore.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)
            .ShouldNotContain("System.Diagnostics.Process",
                "EndpointAgent.Core must stay OS-agnostic (ADR-0005)");
    }

    [Fact]
    public void No_agent_source_file_calls_Process_Start()
    {
        var agentRoot = FindAgentSourceRoot();

        var offenders = Directory
            .EnumerateFiles(agentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Process.Start", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        offenders.ShouldBeEmpty(
            "Process.Start is the shell/launch vector ADR-0005 forbids; found in: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(AgentAssemblies))]
    public void Agent_assemblies_do_not_reference_the_powershell_sdk(string assemblyName)
    {
        var assembly = ResolveAssembly(assemblyName);

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

        references.ShouldNotContain(r => r.StartsWith("System.Management.Automation", StringComparison.Ordinal),
            $"{assemblyName} must not embed PowerShell (ADR-0005)");
        references.ShouldNotContain(r => r.StartsWith("Microsoft.PowerShell", StringComparison.Ordinal),
            $"{assemblyName} must not embed PowerShell (ADR-0005)");
    }

    public static TheoryData<string> AgentAssemblies() =>
        new(AgentCore.GetName().Name!, AgentWindows.GetName().Name!);

    private static Assembly ResolveAssembly(string name) =>
        name == AgentCore.GetName().Name ? AgentCore : AgentWindows;

    /// <summary>Walks up from the test binary to the repo, then to the <c>agent</c> tree.</summary>
    private static string FindAgentSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EndpointPlatform.slnx")))
            {
                var agent = Path.Combine(dir.FullName, "agent");
                Directory.Exists(agent).ShouldBeTrue("expected an 'agent' directory at the repo root");
                return agent;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (EndpointPlatform.slnx).");
    }
}
