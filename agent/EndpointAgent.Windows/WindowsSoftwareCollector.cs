using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads installed software from the Windows uninstall registry keys.
/// </summary>
/// <remarks>
/// <para>
/// Sources: the 64-bit and 32-bit (WOW6432Node) machine uninstall keys and the
/// current-user key. Read-only registry access - no process is launched
/// (ADR-0005). Entries that are updates/patches rather than products are filtered
/// out: no DisplayName, or SystemComponent=1, or a ParentKeyName/ReleaseType
/// update marker.
/// </para>
/// <para>
/// De-duplicated by (name, version): the same product can appear under both the
/// machine and user hives.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSoftwareCollector(ILogger<WindowsSoftwareCollector> logger) : ISoftwareCollector
{
    private readonly ILogger<WindowsSoftwareCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public ValueTask<IReadOnlyList<InventorySoftware>> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var byKey = new Dictionary<string, InventorySoftware>(StringComparer.OrdinalIgnoreCase);

        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallPath, "x64", byKey);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallPathWow, "x86", byKey);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallPath, null, byKey);

        return ValueTask.FromResult<IReadOnlyList<InventorySoftware>>(
            byKey.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private void ReadHive(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string? architecture,
        Dictionary<string, InventorySoftware> accumulator)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(subKeyPath);

            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstallKey.OpenSubKey(subKeyName);
                    if (entry is null)
                    {
                        continue;
                    }

                    var name = (entry.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue; // Updates/patches have no DisplayName.
                    }

                    // Skip system components and Windows updates.
                    if (entry.GetValue("SystemComponent") is int sc && sc == 1)
                    {
                        continue;
                    }

                    if (entry.GetValue("ReleaseType") is string rt
                        && rt.Contains("Update", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.GetValue("ParentKeyName") is string { Length: > 0 })
                    {
                        continue; // An update tied to a parent product.
                    }

                    var software = new InventorySoftware(
                        name,
                        (entry.GetValue("DisplayVersion") as string)?.Trim(),
                        (entry.GetValue("Publisher") as string)?.Trim(),
                        (entry.GetValue("InstallDate") as string)?.Trim(),
                        (entry.GetValue("InstallLocation") as string)?.Trim(),
                        architecture);

                    var dedupeKey = $"{software.Name} {software.Version}";
                    accumulator.TryAdd(dedupeKey, software);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Skipping unreadable uninstall entry {SubKey}.", subKeyName);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read uninstall hive {Hive}\\{Path}.", hive, subKeyPath);
        }
    }
}
