using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// Security posture for one device, as last reported. One row per device.
/// </summary>
/// <remarks>
/// Every check is a nullable bool: null means "unknown" (the agent could not read
/// it, usually for lack of elevation) and is scored separately from a known-bad
/// false. The compliance score is computed by <see cref="ComplianceScore"/> and
/// never stored, so re-weighting checks does not need a data migration.
/// </remarks>
public sealed class DeviceSecurityPosture : AuditableEntity
{
    private DeviceSecurityPosture()
    {
    }

    public DeviceSecurityPosture(Guid deviceId)
    {
        DeviceId = Guard.NotEmpty(deviceId);
    }

    public Guid DeviceId { get; private set; }

    public bool? DefenderAntivirusEnabled { get; private set; }
    public bool? DefenderRealtimeProtectionEnabled { get; private set; }
    public int? DefenderSignatureAgeDays { get; private set; }
    public bool? FirewallDomainEnabled { get; private set; }
    public bool? FirewallPrivateEnabled { get; private set; }
    public bool? FirewallPublicEnabled { get; private set; }
    public bool? SecureBootEnabled { get; private set; }
    public bool? TpmPresent { get; private set; }
    public bool? TpmEnabled { get; private set; }
    public string? TpmSpecVersion { get; private set; }
    public string? BitLockerSystemDriveStatus { get; private set; }
    public int? LocalAdministratorCount { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; }

    public void Apply(
        bool? defenderAv, bool? defenderRtp, int? sigAge,
        bool? fwDomain, bool? fwPrivate, bool? fwPublic,
        bool? secureBoot, bool? tpmPresent, bool? tpmEnabled, string? tpmVersion,
        string? bitLocker, int? localAdmins, DateTimeOffset collectedAt)
    {
        DefenderAntivirusEnabled = defenderAv;
        DefenderRealtimeProtectionEnabled = defenderRtp;
        DefenderSignatureAgeDays = sigAge is >= 0 and <= 3650 ? sigAge : null;
        FirewallDomainEnabled = fwDomain;
        FirewallPrivateEnabled = fwPrivate;
        FirewallPublicEnabled = fwPublic;
        SecureBootEnabled = secureBoot;
        TpmPresent = tpmPresent;
        TpmEnabled = tpmEnabled;
        TpmSpecVersion = Guard.OptionalMaxLength(tpmVersion, 32);
        BitLockerSystemDriveStatus = Guard.OptionalMaxLength(bitLocker, 32);
        LocalAdministratorCount = localAdmins is >= 0 and <= 100000 ? localAdmins : null;
        CollectedAt = collectedAt;
    }

    /// <summary>
    /// A 0-100 compliance score over the checks that could actually be read.
    /// Unknown checks are excluded from both numerator and denominator, so an
    /// unelevated agent that cannot read BitLocker is not penalised for it.
    /// Returns null when nothing was readable.
    /// </summary>
    public int? ComplianceScore()
    {
        var checks = new List<bool>();

        void Add(bool? pass)
        {
            if (pass.HasValue)
            {
                checks.Add(pass.Value);
            }
        }

        Add(DefenderAntivirusEnabled);
        Add(DefenderRealtimeProtectionEnabled);
        Add(DefenderSignatureAgeDays is null ? null : DefenderSignatureAgeDays <= 7);
        Add(FirewallDomainEnabled);
        Add(FirewallPrivateEnabled);
        Add(FirewallPublicEnabled);
        Add(SecureBootEnabled);
        Add(TpmEnabled);
        Add(BitLockerSystemDriveStatus is null ? null : BitLockerSystemDriveStatus == "On");

        if (checks.Count == 0)
        {
            return null;
        }

        return (int)Math.Round(100.0 * checks.Count(c => c) / checks.Count);
    }
}
