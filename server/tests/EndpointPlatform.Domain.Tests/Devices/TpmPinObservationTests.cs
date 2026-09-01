using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// What the platform may conclude about a BitLocker startup PIN from what an
/// endpoint reported.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 is observation only: these tests describe what the device <em>has</em>,
/// never what an administrator requires of it. Joining the two is the policy layer
/// job and is deliberately not modelled here.
/// </para>
/// <para>
/// The rule the whole class exists to hold down is the one the codebase already
/// applies to encryption itself: <b>a read that did not answer is a blind spot, not
/// an absence.</b> A machine whose protector list would not load must never be
/// reported as having no PIN, exactly as an unreadable volume is never reported as
/// unencrypted.
/// </para>
/// </remarks>
public sealed class TpmPinObservationTests
{
    private const string OsVolume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string DataVolume = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";

    private const int OperatingSystem = 0;
    private const int FixedData = 1;
    private const int FullyEncrypted = 1;
    private const int ProtectionOn = 1;

    private static BitLockerVolumeView Volume(
        string id = OsVolume,
        int volumeType = OperatingSystem,
        bool? hasTpm = true,
        bool? hasTpmPin = false,
        bool? hasRecoveryPassword = true) =>
        new(id, "C:", volumeType, FullyEncrypted, ProtectionOn,
            hasRecoveryPassword, hasTpm, hasTpmPin);

    private static TpmPinObservation Observe(
        BitLockerVolumeView[] volumes,
        BitLockerAvailability availability = BitLockerAvailability.Available,
        bool? tpmPresent = true,
        bool? tpmEnabled = true) =>
        BitLockerPosture.Evaluate(availability, volumes, tpmPresent, tpmEnabled).TpmPin;

    // ---- the two answers ---------------------------------------------------

    [Fact]
    public void A_volume_with_a_tpm_pin_protector_is_configured()
    {
        Observe([Volume(hasTpmPin: true)]).ShouldBe(TpmPinObservation.Configured);
    }

    /// <summary>
    /// The case the feature exists for. TPM alone unlocks with nobody present, so it
    /// is not a weaker kind of configured -- it is not configured.
    /// </summary>
    [Fact]
    public void A_volume_with_only_a_tpm_protector_is_not_configured()
    {
        Observe([Volume(hasTpm: true, hasTpmPin: false)])
            .ShouldBe(TpmPinObservation.NotConfigured);
    }

    // ---- not applicable, which is an answer --------------------------------

    [Theory]
    [InlineData(false, true)]   // no TPM
    [InlineData(true, false)]   // TPM present but switched off
    [InlineData(false, false)]
    public void A_device_without_a_usable_tpm_can_never_hold_one(bool present, bool enabled)
    {
        // Not "not configured": nothing on this platform could configure it, so
        // listing it as remediable would send an operator at a button that cannot work.
        Observe([Volume(hasTpmPin: false)], tpmPresent: present, tpmEnabled: enabled)
            .ShouldBe(TpmPinObservation.NotApplicable);
    }

    [Fact]
    public void A_windows_edition_without_bitlocker_is_not_applicable()
    {
        Observe([], availability: BitLockerAvailability.NotAvailable)
            .ShouldBe(TpmPinObservation.NotApplicable);
    }

    // ---- blind spots, which are not answers --------------------------------

    /// <summary>
    /// The most important test here. An unelevated agent cannot read protectors, and
    /// reporting that as "no PIN" would make a correctly protected estate look like
    /// a remediation queue.
    /// </summary>
    [Fact]
    public void An_unreadable_protector_list_is_unknown_not_unconfigured()
    {
        Observe([Volume(hasTpm: null, hasTpmPin: null)]).ShouldBe(TpmPinObservation.Unknown);
    }

    [Theory]
    [InlineData(BitLockerAvailability.AccessDenied)]
    [InlineData(BitLockerAvailability.Error)]
    [InlineData(BitLockerAvailability.Unknown)]
    public void A_query_that_did_not_succeed_is_unknown(BitLockerAvailability availability)
    {
        // Even with a volume on file claiming a PIN: the report is untrustworthy.
        Observe([Volume(hasTpmPin: true)], availability: availability)
            .ShouldBe(TpmPinObservation.Unknown);
    }

    [Fact]
    public void An_unread_tpm_leaves_an_absent_protector_undecidable()
    {
        // The protector is genuinely absent, but whether one could ever be added is
        // not known, so neither NotConfigured nor NotApplicable can be justified.
        Observe([Volume(hasTpmPin: false)], tpmPresent: null, tpmEnabled: null)
            .ShouldBe(TpmPinObservation.Unknown);
    }

    [Fact]
    public void No_operating_system_volume_is_unknown()
    {
        Observe([Volume(id: DataVolume, volumeType: FixedData, hasTpmPin: true)])
            .ShouldBe(TpmPinObservation.Unknown);
    }

    // ---- the verdict is about the operating-system volume alone ------------

    /// <summary>
    /// Startup authentication is a property of the volume Windows boots from. A data
    /// disk cannot have one, and must not be able to answer for the machine.
    /// </summary>
    [Fact]
    public void A_data_volume_never_decides_the_verdict()
    {
        BitLockerVolumeView[] volumes =
        [
            Volume(id: DataVolume, volumeType: FixedData, hasTpmPin: true),
            Volume(id: OsVolume, volumeType: OperatingSystem, hasTpmPin: false),
        ];

        Observe(volumes).ShouldBe(TpmPinObservation.NotConfigured);
    }

    // ---- independence from everything already modelled ---------------------

    /// <summary>
    /// Startup authentication and encryption are separate facts. A fully protected
    /// volume with no PIN is both Protected and NotConfigured, and neither reading
    /// may be inferred from the other.
    /// </summary>
    [Fact]
    public void Readiness_and_startup_authentication_are_independent()
    {
        var result = BitLockerPosture.Evaluate(
            BitLockerAvailability.Available, [Volume(hasTpmPin: false)],
            tpmPresent: true, tpmEnabled: true);

        result.Readiness.ShouldBe(BitLockerReadiness.Protected);
        result.TpmPin.ShouldBe(TpmPinObservation.NotConfigured);
    }

    /// <summary>
    /// A PIN is not a recovery password and neither implies the other. Asserted
    /// because conflating the two is the central risk in this feature.
    /// </summary>
    [Fact]
    public void The_recovery_password_protector_is_orthogonal_to_the_startup_pin()
    {
        Observe([Volume(hasTpmPin: true, hasRecoveryPassword: false)])
            .ShouldBe(TpmPinObservation.Configured);

        Observe([Volume(hasTpmPin: false, hasRecoveryPassword: true)])
            .ShouldBe(TpmPinObservation.NotConfigured);
    }

    /// <summary>
    /// Adding startup-protector observation must not have disturbed the existing
    /// per-volume classification, which nothing in this change touches.
    /// </summary>
    [Fact]
    public void Volume_classification_is_unaffected_by_the_new_fields()
    {
        var findings = BitLockerPosture
            .Evaluate(BitLockerAvailability.Available, [Volume(hasTpmPin: true)], true, true)
            .Volumes;

        var volume = findings.ShouldHaveSingleItem();

        volume.State.ShouldBe(BitLockerVolumeState.Protected);
        volume.HasRecoveryPasswordProtector.ShouldBe(true);
        volume.HasTpmPinProtector.ShouldBe(true);
    }
}
