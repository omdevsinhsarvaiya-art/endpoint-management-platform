using System.Runtime.Versioning;
using System.Security;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads the Start Menu: every shortcut under the all-users Programs folder and
/// under the Programs folder of every signed-in user.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supplementary, never proof of installation.</b> A shortcut says something
/// once pointed at an executable, which is what an uninstall that left its
/// shortcut behind looks like too. What a shortcut contributes is the executable
/// itself -- a location for an installation that recorded none, the primary
/// binary of one that did, and a pointer to software the registry never knew
/// about. On its own it yields <see cref="DiscoveryConfidence.Referenced"/>.
/// </para>
/// <para>
/// Each shortcut is read as bytes and decoded by <see cref="ShellLink"/>; the
/// shell is never asked to resolve one. A target is kept only when it is an
/// absolute local <c>.exe</c> that exists. Environment-relative targets are
/// expanded against a fixed set of machine variables plus, for a user's own
/// shortcuts, that user's profile -- never the agent's own environment, which
/// belongs to LocalSystem.
/// </para>
/// <para>
/// Bounded in every direction: folder depth, shortcuts per root, and bytes per
/// shortcut. Reparse points are not followed, so a junction loop cannot make the
/// walk unbounded.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartMenuEvidenceSource(ILogger<WindowsStartMenuEvidenceSource> logger)
    : ISoftwareEvidenceSource
{
    private readonly ILogger<WindowsStartMenuEvidenceSource> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>A Start Menu with more shortcuts than this is not a Start Menu.</summary>
    internal const int MaxShortcutsPerRoot = 5_000;

    /// <summary>A shortcut is a few kilobytes; anything larger is not one worth reading.</summary>
    internal const int MaxShortcutBytes = 64 * 1024;

    private const int MaxDepth = 8;

    /// <summary>
    /// The machine-level variables a shortcut may reference. Fixed, and read from
    /// the service's environment only for these names, which are the same for
    /// every account on the machine.
    /// </summary>
    private static readonly string[] MachineVariables =
    [
        "SystemRoot",
        "windir",
        "SystemDrive",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
        "ProgramData",
        "ALLUSERSPROFILE",
        "PUBLIC",
    ];

    /// <inheritdoc />
    public string SourceName => "StartMenu";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = new List<SoftwareEvidence>();
        var machine = MachineEnvironment();

        ReadRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            SoftwareScope.Machine, null, machine, evidence, cancellationToken);

        foreach (var (sid, account) in WindowsUserHives.Loaded())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var programs = WindowsUserHives.StartMenuPrograms(sid);
            if (programs is null)
            {
                continue;
            }

            ReadRoot(programs, SoftwareScope.User, account, UserEnvironment(machine, WindowsUserHives.ProfilePath(sid)), evidence, cancellationToken);
        }

        return ValueTask.FromResult<IReadOnlyList<SoftwareEvidence>>(evidence);
    }

    private void ReadRoot(
        string? root,
        SoftwareScope scope,
        string? account,
        IReadOnlyDictionary<string, string> variables,
        List<SoftwareEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var seen = 0;
        var kept = 0;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = MaxDepth,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++seen > MaxShortcutsPerRoot)
                {
                    _logger.LogWarning("Start Menu root {Root} holds more than {Max} shortcuts; the rest are not read.", root, MaxShortcutsPerRoot);
                    break;
                }

                var target = Target(shortcut, variables);
                if (target is null)
                {
                    continue;
                }

                evidence.Add(new SoftwareEvidence(
                    EvidenceSource.StartMenuShortcut,
                    Name: Path.GetFileNameWithoutExtension(shortcut),
                    Scope: scope,
                    InstalledForUser: account,
                    ExecutablePath: target));
                kept++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Could not finish reading Start Menu root {Root}; {Kept} shortcut(s) were read before that.", root, kept);
        }

        _logger.LogDebug("Start Menu root {Root}: {Kept} of {Seen} shortcut(s) point at a local executable.", root, kept, seen);
    }

    /// <summary>
    /// The executable a shortcut points at, when it is a local <c>.exe</c> that
    /// exists; otherwise null.
    /// </summary>
    internal static string? Target(string shortcutPath, IReadOnlyDictionary<string, string> variables)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(shortcutPath);
            if (!info.Exists || info.Length > MaxShortcutBytes)
            {
                return null;
            }

            bytes = File.ReadAllBytes(shortcutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }

        var link = ShellLink.Parse(bytes);
        if (link is null)
        {
            return null;
        }

        string?[] candidates =
        [
            link.LocalPath,
            ShellLink.ExpandEnvironment(link.EnvironmentPath, name => variables.TryGetValue(name, out var value) ? value : null),
        ];

        foreach (var candidate in candidates)
        {
            var path = ExecutablePath.Normalize(candidate);
            if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Dictionary<string, string> MachineEnvironment()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in MachineVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                variables[name] = value;
            }
        }

        return variables;
    }

    private static Dictionary<string, string> UserEnvironment(Dictionary<string, string> machine, string? profile)
    {
        var variables = new Dictionary<string, string>(machine, StringComparer.OrdinalIgnoreCase);

        if (profile is not null)
        {
            variables["USERPROFILE"] = profile;
            variables["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
            variables["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
        }

        return variables;
    }
}
