using System.Reflection;

namespace EndpointAgent.Core;

/// <summary>
/// The agent's product version, as reported to the management server.
/// </summary>
/// <remarks>
/// <para>
/// Single source of truth. This was previously derived inline in two places
/// (<c>AgentApiClient</c> and <c>HeartbeatLoop</c>), which meant the version the
/// server saw depended on which code path last spoke to it — a difference that
/// would only ever surface as a confusing dashboard reading.
/// </para>
/// <para>
/// The value comes from the assembly, so it is set once in the project file and
/// flows to the MSI filename, the installed product version and the dashboard
/// without anyone maintaining a second copy.
/// </para>
/// </remarks>
public static class AgentVersion
{
    /// <summary>Three-part product version, e.g. <c>1.0.0</c>.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AgentVersion).Assembly;

        // Informational version carries the +sha suffix in CI builds; the server
        // stores a short version string, so the build metadata is trimmed rather
        // than sent and truncated somewhere less obvious.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var trimmed = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
