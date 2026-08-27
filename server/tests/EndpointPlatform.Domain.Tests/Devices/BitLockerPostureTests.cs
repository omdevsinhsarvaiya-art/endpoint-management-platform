using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// Turning reported BitLocker facts into volume states and one readiness verdict.
///
/// The governing rule is that an unanswered question never becomes a reassuring
/// answer. A volume the endpoint could not read is Unknown, never NotEncrypted; a
/// query that was refused leaves readiness Unknown regardless of what volume rows
/// happen to be on file. Getting either backwards would report a fully encrypted
/// estate as plaintext the first time an agent lost its elevation — or, worse, the
/// reverse.
/// </summary>
public sealed class BitLockerPostureTests
{
    private const int FullyDecrypted = 0;
    private const int FullyEncrypted = 1;
    private const int EncryptionInProgress = 2;
    private const int DecryptionInProgress = 3;
    private const int EncryptionPaused = 4;

    private const int ProtectionOff = 0;
    private const int ProtectionOn = 1;

    private const int OsVolume = 0;
    private const int FixedDataVolume = 1;

    private static BitLockerVolumeView Volume(
        int? conversion, int? protection, int? type = OsVolume, string letter = "C:") =>
        new($"\\\\?\\Volume{{{Guid.NewGuid()}}}\\", letter, type, conversion, protection, true);

    // ---- per-volume classification -----------------------------------------

    [Fact]
    public void A_fully_encrypted_and_protected_volume_is_protected()
    {
        BitLockerPosture.ClassifyVolume(FullyEncrypted, ProtectionOn)
            .ShouldBe(BitLockerVolumeState.Protected);
    }

    /// <summary>
    /// Encrypted with protection off is precisely what a suspended BitLocker looks
    /// like: the key sits in the clear so the machine can reboot unattended. Reading
    /// it as protected would hide a deliberately weakened machine.
    /// </summary>
    [Fact]
    public void A_fully_encrypted_volume_with_protection_off_is_suspended_not_protected()
    {
        var state = BitLockerPosture.ClassifyVolume(FullyEncrypted, ProtectionOff);

        state.ShouldBe(BitLockerVolumeState.Suspended);
        state.ShouldNotBe(BitLockerVolumeState.Protected);
    }

    [Fact]
    public void A_fully_decrypted_volume_is_not_encrypted()
    {
        BitLockerPosture.ClassifyVolume(FullyDecrypted, ProtectionOff)
            .ShouldBe(BitLockerVolumeState.NotEncrypted);
    }

    [Theory]
    [InlineData(EncryptionInProgress)]
    [InlineData(EncryptionPaused)]
    public void Conversion_towards_encryption_is_in_progress(int conversion)
    {
        BitLockerPosture.ClassifyVolume(conversion, ProtectionOff)
            .ShouldBe(BitLockerVolumeState.EncryptionInProgress);
    }

    [Fact]
    public void Conversion_towards_decryption_is_reported_as_such()
    {
        BitLockerPosture.ClassifyVolume(DecryptionInProgress, ProtectionOff)
            .ShouldBe(BitLockerVolumeState.DecryptionInProgress);
    }

    /// <summary>
    /// The single most important assertion in this file.
    /// </summary>
    [Fact]
    public void An_unreadable_conversion_status_is_unknown_and_never_unencrypted()
    {
        var state = BitLockerPosture.ClassifyVolume(null, null);

        state.ShouldBe(BitLockerVolumeState.Unknown);
        state.ShouldNotBe(BitLockerVolumeState.NotEncrypted);
    }

    /// <summary>
    /// Encrypted, but protection status unreadable. We cannot tell protected from
    /// suspended, and must not guess the flattering one.
    /// </summary>
    [Fact]
    public void An_encrypted_volume_with_unreadable_protection_is_unknown()
    {
        BitLockerPosture.ClassifyVolume(FullyEncrypted, null)
            .ShouldBe(BitLockerVolumeState.Unknown);
    }

    // ---- readiness ---------------------------------------------------------

    [Fact]
    public void A_refused_query_leaves_readiness_unknown_whatever_volumes_are_on_file()
    {
        var result = BitLockerPosture.Evaluate(
            BitLockerAvailability.AccessDenied,
            [Volume(FullyDecrypted, ProtectionOff)],
            tpmPresent: true,
            tpmEnabled: true);

        result.Readiness.ShouldBe(BitLockerReadiness.Unknown);
        result.Readiness.ShouldNotBe(BitLockerReadiness.ReadyToEncrypt);
    }

    [Theory]
    [InlineData(BitLockerAvailability.Unknown)]
    [InlineData(BitLockerAvailability.AccessDenied)]
    [InlineData(BitLockerAvailability.Error)]
    public void No_usable_answer_never_yields_an_encryption_verdict(BitLockerAvailability availability)
    {
        BitLockerPosture.Evaluate(availability, [], null, null)
            .Readiness.ShouldBe(BitLockerReadiness.Unknown);
    }

    /// <summary>
    /// BitLocker absent from the Windows edition is an answer, not a failure, and it
    /// should not sit in an operator's "unknown, investigate" queue forever.
    /// </summary>
    [Fact]
    public void An_edition_without_bitlocker_is_reported_as_unsupported()
    {
        BitLockerPosture.Evaluate(BitLockerAvailability.NotAvailable, [], null, null)
            .Readiness.ShouldBe(BitLockerReadiness.NotSupported);
    }

    [Fact]
    public void An_encrypted_and_protected_os_volume_makes_the_endpoint_protected()
    {
        BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(FullyEncrypted, ProtectionOn)],
                true, true)
            .Readiness.ShouldBe(BitLockerReadiness.Protected);
    }

    [Fact]
    public void A_suspended_os_volume_is_reported_as_suspended()
    {
        BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(FullyEncrypted, ProtectionOff)],
                true, true)
            .Readiness.ShouldBe(BitLockerReadiness.Suspended);
    }

    [Fact]
    public void An_unencrypted_os_volume_with_a_working_tpm_is_ready_to_encrypt()
    {
        BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(FullyDecrypted, ProtectionOff)],
                tpmPresent: true, tpmEnabled: true)
            .Readiness.ShouldBe(BitLockerReadiness.ReadyToEncrypt);
    }

    /// <summary>
    /// Separated from ReadyToEncrypt because the remedy differs: this machine needs
    /// firmware attention before any bulk encryption action could succeed on it.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, false)]
    public void An_unencrypted_volume_without_a_usable_tpm_is_not_ready(bool present, bool? enabled)
    {
        BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(FullyDecrypted, ProtectionOff)],
                present, enabled)
            .Readiness.ShouldBe(BitLockerReadiness.TpmNotReady);
    }

    /// <summary>
    /// The encryption state is known; whether it could be fixed is not. Reported as
    /// its own state rather than guessed either way.
    /// </summary>
    [Fact]
    public void An_unencrypted_volume_with_unreadable_tpm_state_is_neither_ready_nor_blocked()
    {
        var readiness = BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(FullyDecrypted, ProtectionOff)],
                tpmPresent: null, tpmEnabled: null)
            .Readiness;

        readiness.ShouldBe(BitLockerReadiness.NotEncrypted);
        readiness.ShouldNotBe(BitLockerReadiness.ReadyToEncrypt);
        readiness.ShouldNotBe(BitLockerReadiness.TpmNotReady);
    }

    /// <summary>
    /// A volume mid-decryption is heading towards unencrypted. Calling it ready
    /// would invite an operator to start an encryption that cannot proceed.
    /// </summary>
    [Fact]
    public void A_volume_being_decrypted_is_not_reported_as_ready_to_encrypt()
    {
        var readiness = BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(DecryptionInProgress, ProtectionOff)],
                true, true)
            .Readiness;

        readiness.ShouldNotBe(BitLockerReadiness.ReadyToEncrypt);
        readiness.ShouldBe(BitLockerReadiness.EncryptionInProgress);
    }

    /// <summary>
    /// "Is this laptop encrypted" is a question about its system volume. A data disk
    /// still counts in the volume totals, but it does not decide readiness.
    /// </summary>
    [Fact]
    public void Readiness_is_judged_from_the_operating_system_volume()
    {
        var result = BitLockerPosture.Evaluate(
            BitLockerAvailability.Available,
            [
                Volume(FullyEncrypted, ProtectionOn, OsVolume, "C:"),
                Volume(FullyDecrypted, ProtectionOff, FixedDataVolume, "D:"),
            ],
            true, true);

        result.Readiness.ShouldBe(BitLockerReadiness.Protected);
        result.ProtectedVolumeCount.ShouldBe(1);
        result.UnprotectedVolumeCount.ShouldBe(1);
    }

    [Fact]
    public void An_available_query_with_no_os_volume_leaves_readiness_unknown()
    {
        BitLockerPosture.Evaluate(
                BitLockerAvailability.Available,
                [Volume(FullyDecrypted, ProtectionOff, FixedDataVolume, "D:")],
                true, true)
            .Readiness.ShouldBe(BitLockerReadiness.Unknown);
    }

    [Fact]
    public void Volume_counts_separate_protected_unprotected_and_unknown()
    {
        var result = BitLockerPosture.Evaluate(
            BitLockerAvailability.Available,
            [
                Volume(FullyEncrypted, ProtectionOn, OsVolume, "C:"),
                Volume(FullyDecrypted, ProtectionOff, FixedDataVolume, "D:"),
                Volume(FullyEncrypted, ProtectionOff, FixedDataVolume, "E:"),
                Volume(null, null, FixedDataVolume, "F:"),
            ],
            true, true);

        result.ProtectedVolumeCount.ShouldBe(1);
        result.UnprotectedVolumeCount.ShouldBe(2);
        result.UnknownVolumeCount.ShouldBe(1);
        result.Volumes.Count.ShouldBe(4);
    }

    [Fact]
    public void The_operating_system_volume_is_identified_in_the_findings()
    {
        var result = BitLockerPosture.Evaluate(
            BitLockerAvailability.Available,
            [
                Volume(FullyEncrypted, ProtectionOn, OsVolume, "C:"),
                Volume(FullyDecrypted, ProtectionOff, FixedDataVolume, "D:"),
            ],
            true, true);

        result.Volumes.Count(v => v.IsOperatingSystemVolume).ShouldBe(1);
        result.Volumes.Single(v => v.IsOperatingSystemVolume).DriveLetter.ShouldBe("C:");
    }
}
