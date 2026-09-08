using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads what is running now: one piece of evidence per distinct executable
/// with a running process, outside the Windows directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supplementary, never proof of installation.</b> A process says an
/// executable is present and in use, which is exactly what a portable
/// application looks like -- and what a temporary download looks like too. On
/// its own it yields <see cref="DiscoveryConfidence.Observed"/>, or
/// <see cref="DiscoveryConfidence.Transient"/> when the merger recognises an
/// installer or a run from a temp folder. Attached to an installation whose
/// directory contains it, it is that installation's evidence.
/// </para>
/// <para>
/// The process table is the same complete enumeration Force Stop uses -- every
/// process, not the inventory's largest few hundred. A process whose image path
/// cannot be read (a protected process, or one the agent may not query) has
/// nothing to attribute and is skipped. Processes of the operating system
/// itself -- anything under the Windows directory -- are not software
/// discovery's subject and are not reported here; they remain in the process
/// inventory. The agent's own processes are likewise skipped.
/// </para>
/// <para>
/// Scope is attributed the way installations are: an executable inside a
/// profile directory belongs to that profile's account, and anything else is
/// machine-wide. Nothing here queries a process's token, and nothing here
/// touches a process beyond reading its image path.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunningProcessEvidenceSource(
    IServiceProcessCollector processes,
    ILogger<WindowsRunningProcessEvidenceSource> logger) : ISoftwareEvidenceSource
{
    private readonly IServiceProcessCollector _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    private readonly ILogger<WindowsRunningProcessEvidenceSource> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string SourceName => "RunningProcess";

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var running = await _processes.CollectAllProcessesAsync(cancellationToken);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = AppContext.BaseDirectory;
        var profiles = LoadedProfiles();

        var evidence = new List<SoftwareEvidence>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        var system = 0;

        foreach (var process in running)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ExecutablePath.Normalize(process.ExecutablePath);
            if (path is null)
            {
                unreadable++;
                continue;
            }

            if (ExecutablePath.IsUnder(path, windows) || ExecutablePath.IsUnder(path, self))
            {
                system++;
                continue;
            }

            if (!seen.Add(path))
            {
                continue; // Another process of the same executable: one witness is enough.
            }

            var (scope, account) = Attribute(path, profiles);

            evidence.Add(new SoftwareEvidence(
                EvidenceSource.RunningProcess,
                Name: string.IsNullOrWhiteSpace(process.Name) ? null : process.Name,
                Scope: scope,
                InstalledForUser: account,
                ExecutablePath: path));
        }

        _logger.LogDebug(
            "Running processes: {Total} enumerated, {Distinct} distinct executables reported, {System} under Windows or the agent, {Unreadable} without a readable path.",
            running.Count, evidence.Count, system, unreadable);

        return evidence;
    }

    /// <summary>Scope by where the executable lives: inside a loaded profile, that profile's account; otherwise the machine.</summary>
    private static (SoftwareScope Scope, string? Account) Attribute(string path, IReadOnlyList<(string Directory, string Account)> profiles)
    {
        foreach (var (directory, account) in profiles)
        {
            if (ExecutablePath.IsUnder(path, directory))
            {
                return (SoftwareScope.User, account);
            }
        }

        return (SoftwareScope.Machine, null);
    }

    private static List<(string Directory, string Account)> LoadedProfiles()
    {
        var profiles = new List<(string, string)>();

        foreach (var (sid, account) in WindowsUserHives.Loaded())
        {
            if (WindowsUserHives.ProfilePath(sid) is { } directory)
            {
                profiles.Add((directory, account));
            }
        }

        return profiles;
    }
}
