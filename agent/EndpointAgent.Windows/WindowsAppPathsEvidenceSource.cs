using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads the <c>App Paths</c> execution aliases: "when someone runs this file
/// name, here is the executable".
/// </summary>
/// <remarks>
/// <para>
/// <b>Supplementary, never proof of installation.</b> An alias says something
/// pointed at an executable, which is not the same as an application being
/// installed -- an uninstall that leaves its alias behind looks exactly like one
/// that did not. What this contributes is a <em>location</em> for applications
/// whose uninstall key omits one, and a pointer to applications the uninstall
/// registry never knew about at all. The confidence model keeps that distinction:
/// this evidence alone yields <see cref="DiscoveryConfidence.Referenced"/>.
/// </para>
/// <para>
/// Read-only, and nothing here runs: the executable path is data, never a command.
/// Both machine registry views are read, plus the alias key of every loaded user
/// profile hive, so a per-user alias is attributed to the account that holds it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppPathsEvidenceSource(ILogger<WindowsAppPathsEvidenceSource> logger)
    : ISoftwareEvidenceSource
{
    private readonly ILogger<WindowsAppPathsEvidenceSource> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string AppPathsWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths";

    /// <summary>
    /// A machine with more aliases than this has something wrong with it. Bounded
    /// so a corrupt or hostile registry cannot cost unbounded work.
    /// </summary>
    private const int MaxAliasesPerKey = 5_000;

    /// <inheritdoc />
    public string SourceName => "AppPaths";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = new List<SoftwareEvidence>();

        ReadMachine(RegistryView.Registry64, AppPaths, "x64", evidence);
        ReadMachine(RegistryView.Registry32, AppPathsWow, "x86", evidence);

        foreach (var hive in WindowsUserHives.Loaded())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var key = WindowsUserHives.OpenUserSubKey(hive.Sid, AppPaths);
            if (key is not null)
            {
                Read(key, null, SoftwareScope.User, hive.Account, evidence);
            }
        }

        _logger.LogDebug("App Paths contributed {Count} evidence item(s).", evidence.Count);

        return ValueTask.FromResult<IReadOnlyList<SoftwareEvidence>>(evidence);
    }

    private void ReadMachine(
        RegistryView view, string subKeyPath, string registryView, List<SoftwareEvidence> accumulator)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(subKeyPath);
            if (key is not null)
            {
                Read(key, registryView, SoftwareScope.Machine, null, accumulator);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read App Paths at {Path}.", subKeyPath);
        }
    }

    private void Read(
        RegistryKey appPaths,
        string? registryView,
        SoftwareScope scope,
        string? account,
        List<SoftwareEvidence> accumulator)
    {
        string[] aliases;
        try
        {
            aliases = appPaths.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return;
        }

        var seen = 0;

        foreach (var alias in aliases)
        {
            if (++seen > MaxAliasesPerKey)
            {
                _logger.LogWarning("Stopping App Paths enumeration at {Max} aliases.", MaxAliasesPerKey);
                break;
            }

            try
            {
                using var entry = appPaths.OpenSubKey(alias);
                if (entry is null)
                {
                    continue;
                }

                // The default value is the executable. An alias whose default is
                // empty is a real shape -- OneDriveFileLauncher.exe on the machine
                // this was written against -- and is simply not evidence of
                // anything.
                var target = ExecutablePath.Normalize(entry.GetValue(null) as string);
                if (target is null)
                {
                    continue;
                }

                accumulator.Add(new SoftwareEvidence(
                    EvidenceSource.AppPaths,
                    RegistryView: registryView,
                    Scope: scope,
                    InstalledForUser: scope == SoftwareScope.User ? account : null,
                    ExecutablePath: target));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping unreadable App Paths alias {Alias}.", alias);
            }
        }
    }
}
