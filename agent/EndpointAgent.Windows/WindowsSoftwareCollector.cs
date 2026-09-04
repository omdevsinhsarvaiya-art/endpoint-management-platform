using System.Runtime.Versioning;
using System.Security.Principal;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads installed software from the Windows uninstall registry keys.
/// </summary>
/// <remarks>
/// <para>
/// Sources: the 64-bit and 32-bit (WOW6432Node) machine uninstall keys, and the
/// per-user uninstall key of every profile hive Windows currently has loaded.
/// Read-only registry access - no process is launched (ADR-0005). Entries that
/// are updates/patches rather than products are filtered out: no DisplayName, or
/// SystemComponent=1, or a ParentKeyName/ReleaseType update marker.
/// </para>
/// <para>
/// <b>Why HKEY_USERS and not HKCU.</b> The agent runs as LocalSystem, so
/// <see cref="RegistryHive.CurrentUser"/> resolves to the SYSTEM account's own
/// profile - which has no installed software at all. Reading it produced exactly
/// nothing on every machine in the fleet while appearing to cover per-user
/// installs, so applications that install into a user profile by default (Zoom,
/// Teams, Discord, VS Code's user installer, and most Electron apps) were
/// invisible. Enumerating HKEY_USERS reads the real users' hives instead.
/// </para>
/// <para>
/// <b>What this still does not see.</b> Only hives Windows has already mounted,
/// which means signed-in users. A user who is fully signed out has no loaded
/// hive, and their per-user software is not reported until they next sign in.
/// Reading it anyway would mean loading NTUSER.DAT with RegLoadKey - a write to
/// the registry of a profile the agent does not own, which can fail on a locked
/// or roaming profile and, if a hive were left mounted, block that user's next
/// logon. Under-reporting a signed-out user's applications is the safer failure,
/// and it is recorded here rather than left for someone to rediscover.
/// </para>
/// <para>
/// Normalization, de-duplication and length-clamping are
/// <see cref="SoftwareInventoryNormalizer"/>'s job, so they are testable without
/// a machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSoftwareCollector(
    ILogger<WindowsSoftwareCollector> logger,
    WindowsInstallLocationResolver installLocationResolver) : ISoftwareCollector
{
    private readonly ILogger<WindowsSoftwareCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private readonly WindowsInstallLocationResolver _installLocationResolver = installLocationResolver
        ?? throw new ArgumentNullException(nameof(installLocationResolver));

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public ValueTask<IReadOnlyList<InventorySoftware>> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var discovered = new List<DiscoveredSoftware>();

        ReadMachineHive(RegistryView.Registry64, UninstallPath, "x64", discovered);
        ReadMachineHive(RegistryView.Registry32, UninstallPathWow, "x86", discovered);
        ReadLoadedUserHives(discovered, cancellationToken);

        return ValueTask.FromResult(SoftwareInventoryNormalizer.Normalize(discovered));
    }

    private void ReadMachineHive(
        RegistryView view, string subKeyPath, string registryView, List<DiscoveredSoftware> accumulator)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            ReadUninstallKey(baseKey, subKeyPath, registryView, SoftwareScope.Machine, null, accumulator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read machine uninstall hive {Path}.", subKeyPath);
        }
    }

    /// <summary>
    /// Reads the uninstall key of every real user profile hive currently loaded.
    /// </summary>
    /// <remarks>
    /// HKEY_USERS holds one subkey per mounted hive, named by SID. Three of those
    /// are not people - S-1-5-18/19/20 are SYSTEM and the two service accounts -
    /// and every hive may also appear with a <c>_Classes</c> suffix holding COM
    /// registration rather than installed software. Both are skipped: including
    /// them would add noise and, for SYSTEM, re-introduce exactly the empty read
    /// this method replaced.
    /// </remarks>
    private void ReadLoadedUserHives(List<DiscoveredSoftware> accumulator, CancellationToken cancellationToken)
    {
        string[] sids;
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            sids = users.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate loaded user hives; per-user software is not reported.");
            return;
        }

        var profiles = 0;

        foreach (var sid in sids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsRealUserSid(sid))
            {
                continue;
            }

            var account = ResolveAccountName(sid);

            try
            {
                using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                using var hive = users.OpenSubKey(sid);
                if (hive is null)
                {
                    continue;
                }

                profiles++;

                // A per-user install writes to whichever view its installer ran
                // under, so both are read. The scope is what matters here; the
                // view is recorded but is not the binary's architecture.
                ReadUninstallKey(hive, UninstallPath, null, SoftwareScope.User, account, accumulator);
                ReadUninstallKey(hive, UninstallPathWow, "x86", SoftwareScope.User, account, accumulator);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // One unreadable profile must not cost the rest of the inventory.
                _logger.LogDebug(ex, "Skipping unreadable user hive {Sid}.", sid);
            }
        }

        _logger.LogDebug("Read per-user software from {Count} loaded profile hive(s).", profiles);
    }

    /// <summary>
    /// Whether this HKEY_USERS subkey is a human's profile hive.
    /// </summary>
    /// <remarks>
    /// Real accounts are S-1-5-21-... (local or domain) or S-1-12-1-... (Entra).
    /// The well-known service SIDs and the <c>_Classes</c> companions are not.
    /// </remarks>
    private static bool IsRealUserSid(string sid) =>
        !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)
        && (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
            || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The account a SID names, falling back to the SID itself.</summary>
    /// <remarks>
    /// A deleted or unresolvable account still had software installed, so the SID
    /// is reported rather than dropping the entry: an unattributed application is
    /// more useful than a missing one.
    /// </remarks>
    private static string ResolveAccountName(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid;
        }
    }

    private void ReadUninstallKey(
        RegistryKey baseKey,
        string subKeyPath,
        string? registryView,
        SoftwareScope scope,
        string? account,
        List<DiscoveredSoftware> accumulator)
    {
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

                var productCode = ProductCodeOrNull(subKeyName);

                // Most uninstall keys omit InstallLocation -- 22 of 36 on the
                // machine this was measured on -- and without it an application
                // cannot be linked to its processes. Recovered from what Windows
                // Installer recorded, or from a DisplayIcon that genuinely points
                // at the application, and left null when neither does.
                var installLocation = (entry.GetValue("InstallLocation") as string)?.Trim();
                if (string.IsNullOrWhiteSpace(installLocation))
                {
                    installLocation = _installLocationResolver.Resolve(
                        productCode, entry.GetValue("DisplayIcon") as string);
                }

                accumulator.Add(new DiscoveredSoftware(
                    name,
                    (entry.GetValue("DisplayVersion") as string)?.Trim(),
                    (entry.GetValue("Publisher") as string)?.Trim(),
                    (entry.GetValue("InstallDate") as string)?.Trim(),
                    installLocation,
                    registryView,
                    scope,
                    account,
                    // An MSI product's uninstall key is named for its product
                    // code, which is what a managed package records too - so this
                    // is the join between "installed" and "approved".
                    productCode));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping unreadable uninstall entry {SubKey}.", subKeyName);
            }
        }
    }

    /// <summary>The subkey name when it is a product code GUID, else null.</summary>
    private static string? ProductCodeOrNull(string subKeyName) =>
        subKeyName.Length == 38
        && subKeyName[0] == '{'
        && subKeyName[^1] == '}'
        && Guid.TryParse(subKeyName, out _)
            ? subKeyName
            : null;
}
