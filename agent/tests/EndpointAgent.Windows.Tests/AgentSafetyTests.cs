using System.Reflection;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Enforces the agent's "no shell execution" rule (ADR-0005).
/// </summary>
/// <remarks>
/// The agent runs as LocalSystem. The single most dangerous pattern it could adopt
/// is composing a command string and handing it to a shell, because every string
/// that touches that path becomes a potential privileged injection. The rule is
/// therefore absolute - not "be careful with Process.Start" but "the agent
/// assemblies do not reference process creation at all". Windows work is done via
/// APIs (Win32, WMI, DirectoryServices), which have no command line to inject into.
/// When a future feature genuinely needs to launch a process (approved-script
/// execution in Phase 10), it will be added behind a reviewed, signed-script
/// pipeline and this test will be tightened to allow exactly that call site.
/// </remarks>
public sealed class AgentSafetyTests
{
    private static readonly Assembly AgentCore = typeof(EndpointAgent.Core.Configuration.AgentOptions).Assembly;
    private static readonly Assembly AgentWindows = typeof(EndpointAgent.Windows.WindowsSystemInfoProvider).Assembly;

    [Theory]
    [MemberData(nameof(AgentAssemblies))]
    public void Agent_assemblies_do_not_reference_process_creation(string assemblyName)
    {
        var assembly = ResolveAssembly(assemblyName);

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

        references.ShouldNotContain("System.Diagnostics.Process",
            $"{assemblyName} must not launch processes; use native APIs instead (ADR-0005)");
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
}
