using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Enrollment;

namespace EndpointPlatform.Domain.Tests.BitLocker;

/// <summary>
/// Which devices may escrow automatically, and how the two escrow origins differ.
/// </summary>
/// <remarks>
/// Eligibility hangs off the credential rather than the device, and that placement
/// is the design rather than an implementation detail: pinning is established
/// during authenticated enrollment, and re-enrollment revokes credentials, so trust
/// cannot outlive the exchange that created it.
/// </remarks>
public sealed class AutomaticEscrowEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private const string Fingerprint =
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static AgentCredential Credential() =>
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64), Now);

    /// <summary>
    /// The property that keeps every already-enrolled machine out of automatic
    /// escrow until it re-enrolls. Trust-on-first-use was considered and rejected.
    /// </summary>
    [Fact]
    public void A_credential_without_a_pinned_fingerprint_is_not_eligible()
    {
        var credential = Credential();

        credential.SealingKeyFingerprint.ShouldBeNull();
        credential.IsAutomaticEscrowEligible.ShouldBeFalse();
    }

    [Fact]
    public void Pinning_a_fingerprint_makes_the_credential_eligible()
    {
        var credential = Credential();
        credential.PinSealingKey(Fingerprint);

        credential.SealingKeyFingerprint.ShouldBe(Fingerprint);
        credential.IsAutomaticEscrowEligible.ShouldBeTrue();
    }

    /// <summary>
    /// Write-once. A pin that could be overwritten in place would be a suggestion,
    /// and rotation is deliberately a new credential rather than an edit.
    /// </summary>
    [Fact]
    public void A_pinned_fingerprint_cannot_be_replaced()
    {
        var credential = Credential();
        credential.PinSealingKey(Fingerprint);

        Should.Throw<InvalidOperationException>(() => credential.PinSealingKey(new string('c', 64)));

        credential.SealingKeyFingerprint.ShouldBe(Fingerprint);
    }

    /// <summary>
    /// Revocation withdraws eligibility with the credential. Re-enrollment revokes
    /// every active credential, so a device cannot keep escrowing under trust it
    /// has stopped holding.
    /// </summary>
    [Fact]
    public void Revoking_a_credential_withdraws_eligibility()
    {
        var credential = Credential();
        credential.PinSealingKey(Fingerprint);

        credential.Revoke(Now);

        credential.IsAutomaticEscrowEligible.ShouldBeFalse();
    }

    // ---- origins ----------------------------------------------------------

    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string Protector = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    /// <summary>
    /// Backward compatibility, asserted rather than assumed: the constructor the
    /// manual path has always used still produces a manual, symmetrically sealed
    /// record, and the migration backfills existing rows to match.
    /// </summary>
    [Fact]
    public void A_manually_escrowed_record_keeps_its_original_shape()
    {
        var escrow = new BitLockerRecoveryEscrow(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Volume, Protector, "C:",
            "sealed", 1, Guid.CreateVersion7(), "admin@test.local", Now);

        escrow.Origin.ShouldBe(BitLockerEscrowOrigin.Manual);
        escrow.SealScheme.ShouldBe(BitLockerSealScheme.AesGcmV1);
        escrow.EscrowedByUserId.ShouldNotBeNull();
    }

    [Fact]
    public void An_automatically_escrowed_record_has_no_human_actor()
    {
        var escrow = BitLockerRecoveryEscrow.Automatic(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Volume, Protector, "C:",
            "envelope", 1, "LAPTOP-01 (agent)", Now);

        escrow.Origin.ShouldBe(BitLockerEscrowOrigin.Automatic);
        escrow.SealScheme.ShouldBe(BitLockerSealScheme.HybridRsaV1);

        // No administrator was involved, and inventing one would put a fictional
        // actor into the audit trail.
        escrow.EscrowedByUserId.ShouldBeNull();
        escrow.EscrowedByDisplay.ShouldBe("LAPTOP-01 (agent)");
        escrow.IsActive.ShouldBeTrue();
    }

    /// <summary>
    /// Both origins share the supersede model, so rotation behaves identically
    /// whichever way the key was filed.
    /// </summary>
    [Fact]
    public void An_automatic_escrow_supersedes_like_any_other()
    {
        var escrow = BitLockerRecoveryEscrow.Automatic(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Volume, Protector, "C:",
            "envelope", 1, "LAPTOP-01 (agent)", Now);

        var replacement = Guid.CreateVersion7();

        escrow.TrySupersede(replacement, Now.AddHours(1)).ShouldBeTrue();
        escrow.IsActive.ShouldBeFalse();
        escrow.SupersededById.ShouldBe(replacement);
    }

    /// <summary>Protector ids normalise identically on both paths.</summary>
    [Fact]
    public void Brace_style_does_not_create_a_second_escrow_identity()
    {
        var device = Guid.CreateVersion7();
        var org = Guid.CreateVersion7();

        var braced = BitLockerRecoveryEscrow.Automatic(
            org, device, Volume, "{" + Protector.ToUpperInvariant() + "}", "C:", "e", 1, "agent", Now);

        var bare = BitLockerRecoveryEscrow.Automatic(
            org, device, Volume, Protector, "C:", "e", 1, "agent", Now);

        braced.KeyProtectorId.ShouldBe(bare.KeyProtectorId);
    }

    [Fact]
    public void Only_known_seal_schemes_are_recognised()
    {
        BitLockerSealScheme.IsKnown(BitLockerSealScheme.AesGcmV1).ShouldBeTrue();
        BitLockerSealScheme.IsKnown(BitLockerSealScheme.HybridRsaV1).ShouldBeTrue();
        BitLockerSealScheme.IsKnown("something-else").ShouldBeFalse();
        BitLockerSealScheme.IsKnown(null).ShouldBeFalse();
    }
}
