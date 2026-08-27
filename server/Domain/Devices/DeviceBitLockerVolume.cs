using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// Whether an endpoint could answer BitLocker questions, one row per device.
/// </summary>
/// <remarks>
/// Kept apart from the volume rows so that "no volumes" and "could not ask" are
/// distinguishable. Without it, an agent that lost its elevation would report an
/// empty volume list indistinguishable from a machine with nothing encryptable, and
/// the estate would appear to decrypt itself.
/// </remarks>
public sealed class DeviceBitLockerStatus : AuditableEntity
{
    private DeviceBitLockerStatus()
    {
    }

    public DeviceBitLockerStatus(Guid deviceId)
    {
        DeviceId = Guard.NotEmpty(deviceId);
    }

    public Guid DeviceId { get; private set; }

    public BitLockerAvailability Availability { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    public void Apply(BitLockerAvailability availability, DateTimeOffset collectedAt)
    {
        Availability = availability;
        CollectedAt = collectedAt;
    }
}

/// <summary>
/// One encryptable volume on a managed endpoint, as last reported.
/// Replaced wholesale per inventory upload.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no field on this entity, and no column in its table, that
/// could hold a recovery key. The platform records that a recovery-password
/// protector <em>exists</em> and the GUID that identifies it; the 48-digit password
/// itself is never requested from Windows, never transmitted, and has nowhere to be
/// stored if it were.
/// </para>
/// <para>
/// The volume state is not stored. It is derived by <see cref="BitLockerPosture"/>
/// from the raw conversion and protection statuses on read, so the row stays what
/// Windows said.
/// </para>
/// </remarks>
public sealed class DeviceBitLockerVolume : AuditableEntity
{
    private DeviceBitLockerVolume()
    {
        DeviceIdentifier = null!;
    }

    public DeviceBitLockerVolume(
        Guid deviceId,
        string deviceIdentifier,
        string? driveLetter,
        string? persistentVolumeId,
        int? volumeType,
        int? conversionStatus,
        int? protectionStatus,
        int? encryptionPercentage,
        int? encryptionMethod,
        bool? hasRecoveryPasswordProtector,
        string? recoveryProtectorIds,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        DeviceIdentifier = Guard.NotNullOrWhiteSpace(deviceIdentifier, nameof(deviceIdentifier), maxLength: 256);
        DriveLetter = Guard.OptionalMaxLength(driveLetter, 8);
        PersistentVolumeId = Guard.OptionalMaxLength(persistentVolumeId, 128);
        VolumeType = volumeType;
        ConversionStatus = conversionStatus;
        ProtectionStatus = protectionStatus;

        // Windows reports 0-100. Anything else did not come from the API and is
        // recorded as unknown rather than shown to an operator as progress.
        EncryptionPercentage = encryptionPercentage is >= 0 and <= 100 ? encryptionPercentage : null;

        EncryptionMethod = encryptionMethod;
        HasRecoveryPasswordProtector = hasRecoveryPasswordProtector;
        RecoveryProtectorIds = Guard.OptionalMaxLength(recoveryProtectorIds, 1024);
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>The volume's device identifier, e.g. <c>\\?\Volume{guid}\</c>.</summary>
    public string DeviceIdentifier { get; private set; }

    /// <summary>e.g. "C:". Null for a volume with no mount letter.</summary>
    public string? DriveLetter { get; private set; }

    public string? PersistentVolumeId { get; private set; }

    /// <summary>0 operating system, 1 fixed data, 2 removable. Null when unread.</summary>
    public int? VolumeType { get; private set; }

    /// <summary>Raw Win32_EncryptableVolume conversion status. Null when unread.</summary>
    public int? ConversionStatus { get; private set; }

    /// <summary>Raw Win32_EncryptableVolume protection status. Null when unread.</summary>
    public int? ProtectionStatus { get; private set; }

    public int? EncryptionPercentage { get; private set; }

    public int? EncryptionMethod { get; private set; }

    /// <summary>
    /// Whether a recovery-password protector exists. Presence only -- the password
    /// behind it is never read.
    /// </summary>
    public bool? HasRecoveryPasswordProtector { get; private set; }

    /// <summary>
    /// Comma-separated protector GUIDs, which identify protectors without revealing
    /// anything about them. These are identifiers, not secrets: knowing one does not
    /// unlock a volume, and the value that would is never fetched.
    /// </summary>
    public string? RecoveryProtectorIds { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>This row as the view the posture evaluator consumes.</summary>
    public BitLockerVolumeView ToView() => new(
        DeviceIdentifier, DriveLetter, VolumeType, ConversionStatus, ProtectionStatus,
        HasRecoveryPasswordProtector);
}
