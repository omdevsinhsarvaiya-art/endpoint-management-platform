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
    ILogger<WindowsSecurityPostureCollector> logger) : ISecurityPostureCollector, IBitLockerCollector
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
        var bitLocker = ReadBitLockerSystemDriveStatus();
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

    /// <summary>
    /// The system drive's protection status as the single string the security
    /// posture has always carried: "On", "Off", "Unknown", or null when unreadable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preserved exactly, including the null-versus-"Unknown" distinction, because
    /// <c>DeviceSecurityPosture.ComplianceScore</c> scores this field and treats null
    /// as a check that could not be run rather than a failed one. Widening BitLocker
    /// reporting must not silently re-weight every endpoint's compliance score.
    /// </para>
    /// <para>
    /// Now derived from the same volume enumeration the detailed collection uses, so
    /// the summary field and the volume list cannot disagree about one machine.
    /// </para>
    /// </remarks>
    private string? ReadBitLockerSystemDriveStatus()
    {
        var (availability, volumes) = ReadEncryptableVolumes();

        if (availability != BitLockerAvailabilityAvailable)
        {
            return null;
        }

        var systemDrive = SystemDriveLetter();

        var volume = volumes.FirstOrDefault(v =>
            string.Equals(v.DriveLetter, systemDrive, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
        {
            return null;
        }

        return volume.ProtectionStatus switch
        {
            0 => "Off",
            1 => "On",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// The full per-volume BitLocker picture.
    /// </summary>
    /// <remarks>
    /// Named apart from the posture collection because both interfaces this class
    /// serves declare a <c>CollectAsync</c> and they differ only by return type,
    /// which C# cannot overload on. The interface method below is implemented
    /// explicitly and delegates here, which also means BitLocker collection cannot be
    /// invoked by accident through a posture-typed reference.
    /// </remarks>
    public ValueTask<InventoryBitLocker> CollectBitLockerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (availability, volumes) = ReadEncryptableVolumes();
        return ValueTask.FromResult(new InventoryBitLocker(availability, volumes));
    }

    ValueTask<InventoryBitLocker> IBitLockerCollector.CollectAsync(CancellationToken cancellationToken) =>
        CollectBitLockerAsync(cancellationToken);

    private const string BitLockerAvailabilityAvailable = "Available";
    private const string BitLockerAvailabilityAccessDenied = "AccessDenied";
    private const string BitLockerAvailabilityNotAvailable = "NotAvailable";
    private const string BitLockerAvailabilityError = "Error";

    /// <summary>Cap on volumes in one snapshot; far above any real machine.</summary>
    private const int MaxVolumes = 64;

    /// <summary>Cap on protector ids recorded per volume.</summary>
    private const int MaxProtectorIdsPerVolume = 16;

    private static string SystemDriveLetter() =>
        Environment.GetFolderPath(Environment.SpecialFolder.System)[..2]; // "C:"

    /// <summary>
    /// Enumerates every encryptable volume through Win32_EncryptableVolume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The WQL is a compile-time constant with no WHERE clause, and the system drive
    /// is picked out in code afterwards. The previous system-drive-only query
    /// interpolated a drive letter into the query text; that value came from
    /// <c>Environment</c> rather than from a caller, so it was not exploitable, but
    /// ADR-0005 asks for constant query text and this is the read that mutation code
    /// will later be built beside.
    /// </para>
    /// <para>
    /// Failures are classified rather than flattened. Access denied -- the ordinary
    /// outcome when the agent is not elevated -- is distinguished from a missing
    /// provider (BitLocker absent from this Windows edition) and from anything else,
    /// because the server must never read any of them as "not encrypted".
    /// </para>
    /// </remarks>
    private (string Availability, IReadOnlyList<InventoryBitLockerVolume> Volumes) ReadEncryptableVolumes()
    {
        var volumes = new List<InventoryBitLockerVolume>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                new ObjectQuery(
                    "SELECT DeviceID, DriveLetter, PersistentVolumeID, ProtectionStatus, VolumeType "
                    + "FROM Win32_EncryptableVolume"));

            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    if (volumes.Count >= MaxVolumes)
                    {
                        break;
                    }

                    var deviceId = row["DeviceID"] as string;
                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        continue;
                    }

                    var (conversionStatus, percentage) = ReadConversionStatus(row);
                    var (hasRecoveryPassword, protectorIds) = ReadRecoveryProtectors(row);

                    volumes.Add(new InventoryBitLockerVolume(
                        DeviceIdentifier: deviceId,
                        DriveLetter: row["DriveLetter"] as string,
                        PersistentVolumeId: row["PersistentVolumeID"] as string,
                        VolumeType: ToInt(row["VolumeType"]),
                        ConversionStatus: conversionStatus,
                        ProtectionStatus: ToInt(row["ProtectionStatus"]),
                        EncryptionPercentage: percentage,
                        EncryptionMethod: ReadEncryptionMethod(row),
                        HasRecoveryPasswordProtector: hasRecoveryPassword,
                        RecoveryProtectorIds: protectorIds));
                }
            }

            return (BitLockerAvailabilityAvailable, volumes);
        }
        catch (ManagementException ex)
        {
            // An invalid namespace or class means BitLocker is not on this edition
            // at all, which is an answer rather than a failure.
            var availability = ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass
                ? BitLockerAvailabilityNotAvailable
                : ex.ErrorCode == ManagementStatus.AccessDenied
                    ? BitLockerAvailabilityAccessDenied
                    : BitLockerAvailabilityError;

            _logger.LogDebug(ex, "BitLocker volume enumeration unavailable ({Availability}).", availability);
            return (availability, []);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "BitLocker volume enumeration access denied (needs elevation).");
            return (BitLockerAvailabilityAccessDenied, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "BitLocker volume enumeration failed.");
            return (BitLockerAvailabilityError, []);
        }
    }

    /// <summary>
    /// Conversion status and encryption percentage from GetConversionStatus.
    /// </summary>
    /// <remarks>
    /// A read-only WMI method taking no parameters. Both values are null when the
    /// call fails, so a volume whose progress could not be read is unknown rather
    /// than zero -- zero percent would read as "not encrypted at all".
    /// </remarks>
    private (int? ConversionStatus, int? Percentage) ReadConversionStatus(ManagementBaseObject volume)
    {
        try
        {
            using var result = ((ManagementObject)volume).InvokeMethod("GetConversionStatus", null, null);

            if (result is null || ToInt(result["ReturnValue"]) != 0)
            {
                return (null, null);
            }

            return (ToInt(result["ConversionStatus"]), ToInt(result["EncryptionPercentage"]));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            _logger.LogDebug(ex, "BitLocker conversion status unreadable for a volume.");
            return (null, null);
        }
    }

    private int? ReadEncryptionMethod(ManagementBaseObject volume)
    {
        try
        {
            using var result = ((ManagementObject)volume).InvokeMethod("GetEncryptionMethod", null, null);

            return result is null || ToInt(result["ReturnValue"]) != 0
                ? null
                : ToInt(result["EncryptionMethod"]);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            _logger.LogDebug(ex, "BitLocker encryption method unreadable for a volume.");
            return null;
        }
    }

    /// <summary>
    /// Whether a recovery-password protector exists, and the GUIDs identifying them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method never retrieves a recovery key.</b> It calls
    /// <c>GetKeyProtectors</c>, which returns protector <em>identifiers</em> only.
    /// The method that returns the 48-digit password --
    /// <c>GetKeyProtectorNumericalPassword</c> -- is deliberately not called here or
    /// anywhere else in this agent, so the key never enters the process, let alone a
    /// payload or a log.
    /// </para>
    /// <para>
    /// Protector type 3 is <c>NumericalPassword</c>, the recovery password. It is
    /// passed as a typed method parameter, never composed into query text.
    /// </para>
    /// </remarks>
    private (bool? HasRecoveryPassword, IReadOnlyList<string>? ProtectorIds) ReadRecoveryProtectors(
        ManagementBaseObject volume)
    {
        const int NumericalPasswordProtector = 3;

        try
        {
            var managementObject = (ManagementObject)volume;

            using var parameters = managementObject.GetMethodParameters("GetKeyProtectors");
            parameters["KeyProtectorType"] = (uint)NumericalPasswordProtector;

            using var result = managementObject.InvokeMethod("GetKeyProtectors", parameters, null);

            if (result is null || ToInt(result["ReturnValue"]) != 0)
            {
                return (null, null);
            }

            var ids = (result["VolumeKeyProtectorID"] as string[] ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Take(MaxProtectorIdsPerVolume)
                .ToArray();

            return (ids.Length > 0, ids);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            _logger.LogDebug(ex, "BitLocker key protectors unreadable for a volume.");
            return (null, null);
        }
    }

    private static int? ToInt(object? value) => value switch
    {
        null => null,
        uint u => u > int.MaxValue ? null : (int)u,
        int i => i,
        ushort us => us,
        byte b => b,
        long l => l is >= int.MinValue and <= int.MaxValue ? (int)l : null,
        _ => null,
    };

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
