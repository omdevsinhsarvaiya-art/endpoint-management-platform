namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// Whether the endpoint could answer questions about BitLocker at all.
/// </summary>
/// <remarks>
/// Separated from every per-volume verdict because the difference between "this
/// machine has no encrypted volumes" and "this machine would not tell us" is the
/// difference between a finding and a blind spot. BitLocker's WMI provider needs
/// elevation, so an agent that loses it reports <see cref="AccessDenied"/> rather
/// than an estate that appears to have silently decrypted itself overnight.
/// </remarks>
public enum BitLockerAvailability
{
    /// <summary>Nothing has been reported, or the report could not be understood.</summary>
    Unknown = 0,

    /// <summary>The provider answered. Volume verdicts can be trusted.</summary>
    Available = 1,

    /// <summary>The provider exists but refused the query, almost always for lack of elevation.</summary>
    AccessDenied = 2,

    /// <summary>
    /// BitLocker is not present on this edition of Windows, so there is nothing to
    /// report. Distinct from a failure: the answer is known and it is "not applicable".
    /// </summary>
    NotAvailable = 3,

    /// <summary>The query failed for some other reason. Never read as unencrypted.</summary>
    Error = 4,
}

/// <summary>
/// What BitLocker is doing on one volume.
/// </summary>
/// <remarks>
/// <para>
/// Derived from two independent Windows facts, because neither alone is sufficient.
/// <c>ConversionStatus</c> says how much of the disk is encrypted;
/// <c>ProtectionStatus</c> says whether the key is protected. A fully encrypted
/// volume whose protection is off is exactly what a suspended BitLocker looks like,
/// and it is a materially weaker state than <see cref="Protected"/> -- the key sits
/// in the clear so the machine can reboot unattended -- so it gets its own state
/// rather than being rounded up to "encrypted".
/// </para>
/// <para>
/// <see cref="Unknown"/> is never inferred from a failed read. A volume the endpoint
/// could not describe is unknown, not unencrypted.
/// </para>
/// </remarks>
public enum BitLockerVolumeState
{
    /// <summary>The volume's state could not be determined.</summary>
    Unknown = 0,

    /// <summary>Fully decrypted. Confirmed, not assumed.</summary>
    NotEncrypted = 1,

    /// <summary>Encryption is running or paused part-way through.</summary>
    EncryptionInProgress = 2,

    /// <summary>Decryption is running or paused part-way through.</summary>
    DecryptionInProgress = 3,

    /// <summary>Fully encrypted and protection is on. The intended end state.</summary>
    Protected = 4,

    /// <summary>
    /// Fully encrypted but protection is off: the key is available without the
    /// protectors. Encrypted on disk, unprotected in practice.
    /// </summary>
    Suspended = 5,
}

/// <summary>
/// One endpoint's BitLocker readiness, judged from its operating-system volume.
/// </summary>
/// <remarks>
/// The distinction that earns <see cref="ReadyToEncrypt"/> and
/// <see cref="TpmNotReady"/> separate places is operational: the first is a machine
/// an administrator can enable encryption on today, the second is one where trying
/// would fail until somebody touches firmware. Listing them together would send an
/// operator to a bulk action that cannot work.
/// </remarks>
public enum BitLockerReadiness
{
    /// <summary>Nothing readable was reported. Never treated as unencrypted.</summary>
    Unknown = 0,

    /// <summary>The operating-system volume is encrypted and protected.</summary>
    Protected = 1,

    /// <summary>Conversion is under way on the operating-system volume.</summary>
    EncryptionInProgress = 2,

    /// <summary>Encrypted, but protection is suspended.</summary>
    Suspended = 3,

    /// <summary>Not encrypted, and the TPM is present and enabled. Encryption can proceed.</summary>
    ReadyToEncrypt = 4,

    /// <summary>
    /// Not encrypted, and the TPM is absent or disabled. Encryption would need
    /// firmware changes or a non-TPM protector first.
    /// </summary>
    TpmNotReady = 5,

    /// <summary>
    /// Not encrypted, and TPM state could not be read, so readiness is undetermined.
    /// The encryption state is known; the ability to fix it is not.
    /// </summary>
    NotEncrypted = 6,

    /// <summary>BitLocker is not available on this edition of Windows.</summary>
    NotSupported = 7,
}

/// <summary>
/// What the endpoint reports about startup authentication on its operating-system
/// volume.
/// </summary>
/// <remarks>
/// <para>
/// This is an <em>observation</em>, not a compliance verdict. It says what protectors
/// the device has, and nothing about what an administrator requires of it: joining
/// the two is the policy layer job, and doing it here would make "compliant"
/// unanswerable for a device no policy applies to.
/// </para>
/// <para>
/// A BitLocker startup PIN is a thing the machine has, never a thing this platform
/// knows. No state here implies the PIN itself was read, because no Windows API
/// returns one.
/// </para>
/// </remarks>
public enum TpmPinObservation
{
    /// <summary>
    /// The protector list could not be read. Never treated as "no PIN configured":
    /// an agent that lost its elevation must not make an estate look unprotected.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A TPM+PIN startup protector is not possible on this device -- no usable TPM,
    /// or BitLocker is not available at all. An answer, not a failure, and distinct
    /// from a device that could have one and does not.
    /// </summary>
    NotApplicable = 1,

    /// <summary>
    /// The device could carry a TPM+PIN protector and does not. Includes the common
    /// case of a volume protected by TPM alone, which is materially weaker: it unlocks
    /// without anyone present.
    /// </summary>
    NotConfigured = 2,

    /// <summary>A TPM+PIN startup protector exists on the operating-system volume.</summary>
    Configured = 3,
}

/// <summary>
/// One volume, reduced to what a verdict needs.
/// </summary>
/// <param name="VolumeType">0 operating system, 1 fixed data, 2 removable. Null when unread.</param>
/// <param name="ConversionStatus">Raw Win32_EncryptableVolume conversion status, or null.</param>
/// <param name="ProtectionStatus">Raw Win32_EncryptableVolume protection status, or null.</param>
/// <param name="HasTpmProtector">TPM-only startup protector presence, or null when unread.</param>
/// <param name="HasTpmPinProtector">TPM+PIN startup protector presence, or null when unread.</param>
public sealed record BitLockerVolumeView(
    string DeviceIdentifier,
    string? DriveLetter,
    int? VolumeType,
    int? ConversionStatus,
    int? ProtectionStatus,
    bool? HasRecoveryPasswordProtector,
    bool? HasTpmProtector = null,
    bool? HasTpmPinProtector = null);

/// <summary>One volume with its state resolved.</summary>
public sealed record BitLockerVolumeFinding(
    string DeviceIdentifier,
    string? DriveLetter,
    bool IsOperatingSystemVolume,
    BitLockerVolumeState State,
    bool? HasRecoveryPasswordProtector,
    bool? HasTpmProtector = null,
    bool? HasTpmPinProtector = null);

/// <param name="ProtectedVolumeCount">Volumes fully encrypted with protection on.</param>
/// <param name="UnprotectedVolumeCount">
/// Volumes known not to be protected -- unencrypted, suspended, or mid-conversion.
/// Volumes whose state is unknown are excluded and counted separately.
/// </param>
/// <param name="UnknownVolumeCount">Volumes whose state could not be determined.</param>
/// <param name="TpmPin">
/// Startup-authentication observation for the operating-system volume. Reported
/// alongside readiness rather than folded into it: a volume can be fully protected
/// and still unlock with nobody present.
/// </param>
public sealed record BitLockerPostureResult(
    BitLockerAvailability Availability,
    BitLockerReadiness Readiness,
    IReadOnlyList<BitLockerVolumeFinding> Volumes,
    int ProtectedVolumeCount,
    int UnprotectedVolumeCount,
    int UnknownVolumeCount,
    TpmPinObservation TpmPin = TpmPinObservation.Unknown);

/// <summary>
/// Turns reported BitLocker facts into per-volume states and one endpoint verdict.
/// </summary>
/// <remarks>
/// Computed on read and never stored, matching
/// <see cref="DeviceSecurityPosture.ComplianceScore"/> and
/// <see cref="DriverHealthSummary"/>: the rows stay what the endpoint said, and
/// re-reading them differently is a code change rather than a migration.
/// </remarks>
public static class BitLockerPosture
{
    // Win32_EncryptableVolume ConversionStatus.
    private const int FullyDecrypted = 0;
    private const int FullyEncrypted = 1;
    private const int EncryptionInProgress = 2;
    private const int DecryptionInProgress = 3;
    private const int EncryptionPaused = 4;
    private const int DecryptionPaused = 5;

    // Win32_EncryptableVolume ProtectionStatus.
    private const int ProtectionOff = 0;
    private const int ProtectionOn = 1;

    /// <summary>The operating-system volume type, from Win32_EncryptableVolume.</summary>
    private const int OperatingSystemVolume = 0;

    /// <summary>
    /// Resolves one volume's state from its conversion and protection status.
    /// </summary>
    /// <remarks>
    /// A null conversion status is <see cref="BitLockerVolumeState.Unknown"/> and
    /// never <see cref="BitLockerVolumeState.NotEncrypted"/>. Getting that backwards
    /// would let an agent that lost its elevation report a fully encrypted estate as
    /// plaintext.
    /// </remarks>
    public static BitLockerVolumeState ClassifyVolume(int? conversionStatus, int? protectionStatus) =>
        conversionStatus switch
        {
            null => BitLockerVolumeState.Unknown,

            FullyDecrypted => BitLockerVolumeState.NotEncrypted,

            // Paused is folded in with running on purpose: both mean the volume is
            // part-converted, which is neither encrypted nor not. The raw status is
            // preserved on the row for anyone who needs the difference.
            EncryptionInProgress or EncryptionPaused => BitLockerVolumeState.EncryptionInProgress,
            DecryptionInProgress or DecryptionPaused => BitLockerVolumeState.DecryptionInProgress,

            FullyEncrypted => protectionStatus switch
            {
                ProtectionOn => BitLockerVolumeState.Protected,

                // Encrypted with protection off is what suspension looks like. The
                // key is exposed, so this must not read as protected.
                ProtectionOff => BitLockerVolumeState.Suspended,

                _ => BitLockerVolumeState.Unknown,
            },

            _ => BitLockerVolumeState.Unknown,
        };

    /// <summary>Evaluates one endpoint's BitLocker posture.</summary>
    /// <param name="availability">Whether the endpoint could query BitLocker at all.</param>
    /// <param name="volumes">The reported volumes. Empty when none were reported.</param>
    /// <param name="tpmPresent">TPM presence, or null when unread.</param>
    /// <param name="tpmEnabled">TPM enabled state, or null when unread.</param>
    public static BitLockerPostureResult Evaluate(
        BitLockerAvailability availability,
        IReadOnlyCollection<BitLockerVolumeView>? volumes,
        bool? tpmPresent,
        bool? tpmEnabled)
    {
        var findings = (volumes ?? [])
            .Select(v => new BitLockerVolumeFinding(
                v.DeviceIdentifier,
                v.DriveLetter,
                v.VolumeType == OperatingSystemVolume,
                ClassifyVolume(v.ConversionStatus, v.ProtectionStatus),
                v.HasRecoveryPasswordProtector,
                v.HasTpmProtector,
                v.HasTpmPinProtector))
            .ToList();

        return new BitLockerPostureResult(
            availability,
            DetermineReadiness(availability, findings, tpmPresent, tpmEnabled),
            findings,
            ProtectedVolumeCount: findings.Count(v => v.State == BitLockerVolumeState.Protected),
            UnprotectedVolumeCount: findings.Count(v =>
                v.State is BitLockerVolumeState.NotEncrypted
                    or BitLockerVolumeState.Suspended
                    or BitLockerVolumeState.EncryptionInProgress
                    or BitLockerVolumeState.DecryptionInProgress),
            UnknownVolumeCount: findings.Count(v => v.State == BitLockerVolumeState.Unknown),
            TpmPin: ObserveTpmPin(availability, findings, tpmPresent, tpmEnabled));
    }

    private static BitLockerReadiness DetermineReadiness(
        BitLockerAvailability availability,
        IReadOnlyList<BitLockerVolumeFinding> findings,
        bool? tpmPresent,
        bool? tpmEnabled)
    {
        if (availability == BitLockerAvailability.NotAvailable)
        {
            return BitLockerReadiness.NotSupported;
        }

        // A query that did not succeed tells us nothing about encryption, whatever
        // volumes happen to be on file.
        if (availability != BitLockerAvailability.Available)
        {
            return BitLockerReadiness.Unknown;
        }

        // Readiness is about the operating-system volume: a data disk left
        // unencrypted is a finding, but it is not what "is this laptop encrypted"
        // means to anyone asking.
        var osVolume = findings.FirstOrDefault(v => v.IsOperatingSystemVolume);

        if (osVolume is null)
        {
            return BitLockerReadiness.Unknown;
        }

        return osVolume.State switch
        {
            BitLockerVolumeState.Protected => BitLockerReadiness.Protected,
            BitLockerVolumeState.Suspended => BitLockerReadiness.Suspended,
            BitLockerVolumeState.EncryptionInProgress => BitLockerReadiness.EncryptionInProgress,

            // Decryption in progress is on its way to unencrypted, and reporting it
            // as ready would invite an operator to "encrypt" a volume that is busy
            // doing the opposite.
            BitLockerVolumeState.DecryptionInProgress => BitLockerReadiness.EncryptionInProgress,

            BitLockerVolumeState.NotEncrypted => (tpmPresent, tpmEnabled) switch
            {
                (true, true) => BitLockerReadiness.ReadyToEncrypt,
                (false, _) or (_, false) => BitLockerReadiness.TpmNotReady,

                // The volume is genuinely unencrypted -- that much is known -- but
                // whether it could be encrypted is not.
                _ => BitLockerReadiness.NotEncrypted,
            },

            _ => BitLockerReadiness.Unknown,
        };
    }

    /// <summary>
    /// Observes startup authentication on the operating-system volume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered so the two answers come before the two blind spots. A device with no
    /// usable TPM cannot carry a TPM+PIN protector at all, so it is
    /// <see cref="TpmPinObservation.NotApplicable"/> and never
    /// <see cref="TpmPinObservation.NotConfigured"/> -- listing it as merely
    /// unconfigured would put it in front of an operator as something to fix by
    /// clicking, when nothing on this platform can fix it.
    /// </para>
    /// <para>
    /// Everything else unreadable is <see cref="TpmPinObservation.Unknown"/>. A
    /// protector list that would not load says nothing about whether a PIN is set,
    /// and calling that unconfigured would misdescribe a protected machine.
    /// </para>
    /// </remarks>
    private static TpmPinObservation ObserveTpmPin(
        BitLockerAvailability availability,
        IReadOnlyList<BitLockerVolumeFinding> findings,
        bool? tpmPresent,
        bool? tpmEnabled)
    {
        // No BitLocker on this edition means no startup protector of any kind.
        if (availability == BitLockerAvailability.NotAvailable)
        {
            return TpmPinObservation.NotApplicable;
        }

        if (availability != BitLockerAvailability.Available)
        {
            return TpmPinObservation.Unknown;
        }

        // A TPM that is absent or switched off cannot hold a TPM+PIN protector.
        // Checked before the volume, because the answer does not depend on one.
        if (tpmPresent is false || tpmEnabled is false)
        {
            return TpmPinObservation.NotApplicable;
        }

        var osVolume = findings.FirstOrDefault(v => v.IsOperatingSystemVolume);

        if (osVolume?.HasTpmPinProtector is not { } hasTpmPin)
        {
            // No operating-system volume reported, or the protector query did not
            // answer. Either way this is a blind spot, not an absence.
            return TpmPinObservation.Unknown;
        }

        if (hasTpmPin)
        {
            return TpmPinObservation.Configured;
        }

        // Genuinely absent, but only sayable when the TPM is known usable: an unread
        // TPM leaves it undecidable whether one could ever be added.
        return tpmPresent is true && tpmEnabled is true
            ? TpmPinObservation.NotConfigured
            : TpmPinObservation.Unknown;
    }
}
