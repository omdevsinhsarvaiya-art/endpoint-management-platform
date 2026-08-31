using System.Management;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads one protector's numerical recovery password through
/// <c>Win32_EncryptableVolume.GetKeyProtectorNumericalPassword</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the deliberate reversal of decision J-4.</b> Until automatic escrow,
/// this agent called <c>GetKeyProtectors</c> -- identifiers only -- and never this
/// method, so a recovery password could not enter the process at all. That
/// property has been given up knowingly: the rationale for the original decision is
/// preserved in <c>docs/threat-model.md</c> alongside the reversal and its cost,
/// which is that the platform now aggregates disk-unlock credentials across the
/// estate rather than holding the few an administrator chose to file.
/// </para>
/// <para>
/// <b>Nothing here decides whether the call is allowed.</b> That is
/// <c>AutomaticEscrowGate</c>'s job, and it runs eligibility, fingerprint pinning
/// and deduplication before this type is reached. Keeping the decision out of here
/// means the reader stays a thin, auditable wrapper over one API call for one
/// protector, with no path that enumerates anything.
/// </para>
/// <para>
/// <b>The managed-string limitation, stated plainly.</b> WMI hands the password
/// back as a <see cref="string"/>. .NET strings are immutable and garbage
/// collected, and the runtime may copy them while compacting the heap, so this
/// value <em>cannot be reliably zeroed</em> -- not here and not by the caller. The
/// buffers that can be scrubbed are scrubbed during sealing; this one cannot be,
/// and claiming otherwise would be false. What is done instead is to narrow the
/// window: the call happens only when an escrow is actually owed, so a machine
/// already escrowed never materialises its password at all, and the value is used
/// once and dropped rather than held or passed around.
/// </para>
/// <para>
/// No overload composes query text. The protector id is passed as a typed method
/// parameter, and the volume is located by an equality match on a constant query.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecoveryPasswordReader(ILogger<WindowsRecoveryPasswordReader> logger)
    : IRecoveryPasswordReader
{
    private const string Namespace = @"root\cimv2\Security\MicrosoftVolumeEncryption";

    /// <summary>Constant. Volumes are filtered in code, never by interpolated WQL.</summary>
    private const string VolumeQuery = "SELECT * FROM Win32_EncryptableVolume";

    private readonly ILogger<WindowsRecoveryPasswordReader> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public Task<RecoveryPasswordReadResult> ReadAsync(
        string volumeDeviceIdentifier,
        string keyProtectorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeDeviceIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyProtectorId);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Read(volumeDeviceIdentifier, keyProtectorId));
    }

    private RecoveryPasswordReadResult Read(string volumeDeviceIdentifier, string keyProtectorId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Namespace), new ObjectQuery(VolumeQuery));

            using var volumes = searcher.Get();

            foreach (var item in volumes)
            {
                using var volume = (ManagementObject)item;

                if (!string.Equals(
                        volume["DeviceID"] as string,
                        volumeDeviceIdentifier,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return ReadFromVolume(volume, keyProtectorId);
            }

            // The volume named at detection is gone. Ordinary on a machine whose
            // disks changed between inventory and this call.
            return RecoveryPasswordReadResult.Failed(
                RecoveryPasswordReadStatus.ProtectorGone, keyProtectorId);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            // The exception is logged without its message being trusted to be
            // free of volume detail, and never with anything from the result.
            _logger.LogWarning(
                "BitLocker recovery password could not be read for protector {Protector}: "
                + "the volume encryption provider refused the request.", keyProtectorId);

            return RecoveryPasswordReadResult.Failed(
                RecoveryPasswordReadStatus.Refused, keyProtectorId);
        }
    }

    private RecoveryPasswordReadResult ReadFromVolume(ManagementObject volume, string keyProtectorId)
    {
        try
        {
            using var parameters = volume.GetMethodParameters("GetKeyProtectorNumericalPassword");

            // Typed parameter. Windows returns the password for this protector
            // alone; there is no call here that returns more than one.
            parameters["VolumeKeyProtectorID"] = keyProtectorId;

            using var result = volume.InvokeMethod("GetKeyProtectorNumericalPassword", parameters, null);

            if (result is null)
            {
                return RecoveryPasswordReadResult.Failed(
                    RecoveryPasswordReadStatus.Refused, keyProtectorId);
            }

            var returnValue = ToInt(result["ReturnValue"]);

            if (returnValue != 0)
            {
                // Distinguished so the retry schedule can tell a protector that
                // vanished from a machine that will keep refusing. The code itself
                // is safe to log; it is a status, not a value.
                var status = returnValue == FveNotFound
                    ? RecoveryPasswordReadStatus.ProtectorGone
                    : RecoveryPasswordReadStatus.Refused;

                _logger.LogWarning(
                    "BitLocker refused to return the recovery password for protector {Protector} "
                    + "(status 0x{Status:X8}).", keyProtectorId, returnValue ?? -1);

                return RecoveryPasswordReadResult.Failed(status, keyProtectorId);
            }

            if (result["NumericalPassword"] is not string password || string.IsNullOrWhiteSpace(password))
            {
                return RecoveryPasswordReadResult.Failed(
                    RecoveryPasswordReadStatus.Malformed, keyProtectorId);
            }

            // Returned, not logged, not stored, not held. The caller validates and
            // seals it immediately.
            return new RecoveryPasswordReadResult(
                RecoveryPasswordReadStatus.Success, password, keyProtectorId);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            _logger.LogWarning(
                "BitLocker recovery password could not be read for protector {Protector}.",
                keyProtectorId);

            return RecoveryPasswordReadResult.Failed(
                RecoveryPasswordReadStatus.Refused, keyProtectorId);
        }
    }

    /// <summary>FVE_E_NOT_FOUND: no such protector on this volume.</summary>
    private const int FveNotFound = unchecked((int)0x80310008);

    private static int? ToInt(object? value) => value switch
    {
        null => null,
        uint u => unchecked((int)u),
        int i => i,
        ushort us => us,
        byte b => b,
        long l => l is >= int.MinValue and <= int.MaxValue ? (int)l : null,
        _ => null,
    };
}
