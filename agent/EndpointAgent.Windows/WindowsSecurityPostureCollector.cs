using System.Management;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads Windows security posture via WMI/CIM and the registry.
/// </summary>
/// <remarks>
/// <para>
/// All read-only, fixed-query, no process launch (ADR-0005). Sources:
/// Defender (root\Microsoft\Windows\Defender: MSFT_MpComputerStatus), firewall
/// profiles (registry), Secure Boot state (registry), TPM
/// (root\CIMV2\Security\MicrosoftTpm: Win32_Tpm), BitLocker
/// (root\CIMV2\Security\MicrosoftVolumeEncryption: Win32_EncryptableVolume),
/// local Administrators membership count (via the local accounts collector).
/// </para>
/// <para>
/// Several of these (BitLocker, TPM) require elevation. When the agent is not
/// elevated the queries fail with access-denied and the corresponding field is
/// reported null. That is deliberate: "unknown" must never be rendered as a
/// false negative that flags a compliant machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityPostureCollector(
    ILocalAccountsCollector localAccountsCollector,
    ILogger<WindowsSecurityPostureCollector> logger) : ISecurityPostureCollector
{
    private readonly ILocalAccountsCollector _localAccountsCollector = localAccountsCollector
        ?? throw new ArgumentNullException(nameof(localAccountsCollector));

    private readonly ILogger<WindowsSecurityPostureCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<InventorySecurityPosture> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (avEnabled, rtpEnabled, sigAge) = ReadDefender();
        var (fwDomain, fwPrivate, fwPublic) = ReadFirewall();
        var secureBoot = ReadSecureBoot();
        var (tpmPresent, tpmEnabled, tpmVersion) = ReadTpm();
        var bitLocker = ReadBitLockerSystemDrive();
        var localAdmins = await CountLocalAdminsAsync(cancellationToken);

        return new InventorySecurityPosture(
            avEnabled, rtpEnabled, sigAge,
            fwDomain, fwPrivate, fwPublic,
            secureBoot,
            tpmPresent, tpmEnabled, tpmVersion,
            bitLocker,
            localAdmins);
    }

    private (bool? Av, bool? Rtp, int? SigAge) ReadDefender()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                new ObjectQuery("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus"));
            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    bool? av = row["AntivirusEnabled"] as bool?;
                    bool? rtp = row["RealTimeProtectionEnabled"] as bool?;
                    int? age = row["AntivirusSignatureAge"] is uint a ? (int)Math.Min(a, int.MaxValue) : null;
                    return (av, rtp, age);
                }
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "Defender status unavailable.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Defender status access denied.");
        }

        return (null, null, null);
    }

    private (bool? Domain, bool? Private, bool? Public) ReadFirewall()
    {
        // Registry is readable without elevation and matches what the firewall
        // control panel shows. Profiles: Domain, Standard(=Private), Public.
        return (
            ReadFirewallProfile("DomainProfile"),
            ReadFirewallProfile("StandardProfile"),
            ReadFirewallProfile("PublicProfile"));
    }

    private bool? ReadFirewallProfile(string profile)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}");
            return key?.GetValue("EnableFirewall") is int enabled ? enabled == 1 : (bool?)null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Firewall profile {Profile} unreadable.", profile);
            return null;
        }
    }

    private bool? ReadSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            // UEFISecureBootEnabled = 1 when Secure Boot is on. Absent key => legacy BIOS.
            return key?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : (bool?)null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Secure Boot state unreadable.");
            return null;
        }
    }

    private (bool? Present, bool? Enabled, string? Version) ReadTpm()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"),
                new ObjectQuery("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm"));
            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    bool? enabled = row["IsEnabled_InitialValue"] as bool?;
                    var version = (row["SpecVersion"] as string)?.Split(',')[0]?.Trim();
                    return (true, enabled, version);
                }
            }

            // Query succeeded but returned no instance => no TPM.
            return (false, null, null);
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "TPM status unavailable (needs elevation, or no TPM).");
            return (null, null, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "TPM status access denied.");
            return (null, null, null);
        }
    }

    private string? ReadBitLockerSystemDrive()
    {
        try
        {
            var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System)[..2]; // "C:"

            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                new ObjectQuery($"SELECT ProtectionStatus, DriveLetter FROM Win32_EncryptableVolume WHERE DriveLetter = '{systemDrive}'"));
            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    var status = row["ProtectionStatus"] as uint?;
                    return status switch
                    {
                        0 => "Off",
                        1 => "On",
                        _ => "Unknown",
                    };
                }
            }

            return null;
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "BitLocker status unavailable (needs elevation).");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "BitLocker status access denied (needs elevation).");
            return null;
        }
    }

    private async Task<int?> CountLocalAdminsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accounts = await _localAccountsCollector.CollectAsync(cancellationToken);
            return accounts.Users.Count(u => u.IsLocalAdministrator);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not count local administrators.");
            return null;
        }
    }
}
