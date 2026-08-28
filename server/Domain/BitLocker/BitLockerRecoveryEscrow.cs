using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.BitLocker;

/// <summary>
/// One escrowed BitLocker recovery password, sealed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This entity cannot hold a plaintext recovery password.</b> The constructor
/// takes an already-sealed value and there is no property, method or overload
/// that accepts or returns plaintext. Sealing happens in the service layer before
/// anything reaches the domain, so a future caller cannot construct one wrongly:
/// the type simply has nowhere to put a password.
/// </para>
/// <para>
/// Deliberately a separate table rather than a column on
/// <c>DeviceBitLockerVolume</c>. That row is replaced wholesale on every inventory
/// upload, so a secret stored there would be destroyed by the next heartbeat --
/// and would travel through the inventory API on the way in.
/// </para>
/// <para>
/// Escrow is keyed on device + volume + protector, because a protector is what a
/// recovery password actually unlocks. A volume can carry several, and a machine
/// re-imaged onto the same hardware gets new ones; keying on anything coarser
/// would eventually hand somebody the wrong key for the right machine.
/// </para>
/// </remarks>
public sealed class BitLockerRecoveryEscrow : Entity
{
    private BitLockerRecoveryEscrow()
    {
        VolumeDeviceIdentifier = null!;
        KeyProtectorId = null!;
        SealedRecoveryPassword = null!;
        EscrowedByDisplay = null!;
    }

    /// <param name="sealedRecoveryPassword">
    /// The AES-GCM envelope produced by the recovery-key protector. Never the
    /// password itself: this type has no way to seal one, which is what stops a
    /// caller from passing plaintext by mistake.
    /// </param>
    public BitLockerRecoveryEscrow(
        Guid organizationId,
        Guid deviceId,
        string volumeDeviceIdentifier,
        string keyProtectorId,
        string? driveLetter,
        string sealedRecoveryPassword,
        int keyVersion,
        Guid escrowedByUserId,
        string escrowedByDisplay,
        DateTimeOffset now)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        DeviceId = Guard.NotEmpty(deviceId, nameof(deviceId));
        VolumeDeviceIdentifier = Guard.NotNullOrWhiteSpace(
            volumeDeviceIdentifier, nameof(volumeDeviceIdentifier), maxLength: 256);

        KeyProtectorId = NormalizeProtectorId(keyProtectorId);
        DriveLetter = Guard.OptionalMaxLength(driveLetter, 8, nameof(driveLetter));

        SealedRecoveryPassword = Guard.NotNullOrWhiteSpace(
            sealedRecoveryPassword, nameof(sealedRecoveryPassword), maxLength: 4096);

        KeyVersion = keyVersion > 0
            ? keyVersion
            : throw new ArgumentOutOfRangeException(nameof(keyVersion), "Key version must be positive.");

        EscrowedByUserId = Guard.NotEmpty(escrowedByUserId, nameof(escrowedByUserId));
        EscrowedByDisplay = Guard.NotNullOrWhiteSpace(escrowedByDisplay, nameof(escrowedByDisplay), 320);
        EscrowedAt = now;
        IsActive = true;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>The volume this key unlocks, e.g. <c>\\?\Volume{guid}\</c>.</summary>
    public string VolumeDeviceIdentifier { get; private set; }

    /// <summary>
    /// The protector GUID this password belongs to. A protector id names a
    /// protector and unlocks nothing on its own.
    /// </summary>
    public string KeyProtectorId { get; private set; }

    /// <summary>Convenience only. The identifier above is the identity.</summary>
    public string? DriveLetter { get; private set; }

    /// <summary>
    /// AES-GCM envelope. Opaque here and never decrypted by the domain.
    /// </summary>
    public string SealedRecoveryPassword { get; private set; }

    /// <summary>
    /// Which escrow key sealed this, so a future re-key can find the rows it must
    /// re-seal without a schema change.
    /// </summary>
    public int KeyVersion { get; private set; }

    public Guid EscrowedByUserId { get; private set; }

    public string EscrowedByDisplay { get; private set; }

    public DateTimeOffset EscrowedAt { get; private set; }

    /// <summary>
    /// False once superseded or deleted. Only one active record may exist per
    /// device + volume + protector, enforced by a partial unique index.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>The escrow that replaced this one, when it was superseded.</summary>
    public Guid? SupersededById { get; private set; }

    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>
    /// How many times this key has been revealed. A cheap misuse signal that
    /// survives even if somebody stops reading the audit trail.
    /// </summary>
    public int RevealedCount { get; private set; }

    public DateTimeOffset? LastRevealedAt { get; private set; }

    public Guid? LastRevealedByUserId { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedByUserId { get; private set; }

    public string? DeletedByDisplay { get; private set; }

    /// <summary>
    /// Marks this record superseded by a newer escrow for the same protector.
    /// </summary>
    /// <remarks>
    /// The old ciphertext is deliberately kept. A machine restored from a backup
    /// taken before the rotation needs the key that was current then, and an
    /// escrow feature that cannot answer that question has failed at the one
    /// moment it exists for. The record stops being active, so it can never be
    /// confused with the current key.
    /// </remarks>
    public bool TrySupersede(Guid replacementId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        SupersededById = Guard.NotEmpty(replacementId, nameof(replacementId));
        SupersededAt = now;
        return true;
    }

    /// <summary>
    /// Soft-deletes the record and destroys the ciphertext.
    /// </summary>
    /// <remarks>
    /// The row survives so the audit trail keeps something to point at, but the
    /// sealed value is overwritten rather than left behind: a deleted key that is
    /// still decryptable is not deleted. This is irreversible by design.
    /// </remarks>
    public bool TryDelete(Guid actorId, string actorDisplay, DateTimeOffset now)
    {
        if (DeletedAt is not null)
        {
            return false;
        }

        SealedRecoveryPassword = DeletedCiphertextMarker;
        IsActive = false;
        DeletedAt = now;
        DeletedByUserId = Guard.NotEmpty(actorId, nameof(actorId));
        DeletedByDisplay = Guard.NotNullOrWhiteSpace(actorDisplay, nameof(actorDisplay), 320);
        return true;
    }

    /// <summary>Records a successful reveal. Does not touch the sealed value.</summary>
    public void RecordReveal(Guid actorId, DateTimeOffset now)
    {
        RevealedCount++;
        LastRevealedAt = now;
        LastRevealedByUserId = actorId;
    }

    /// <summary>Whether this record still holds a key that can be revealed.</summary>
    public bool CanBeRevealed =>
        DeletedAt is null && SealedRecoveryPassword != DeletedCiphertextMarker;

    /// <summary>
    /// Written over the ciphertext on delete. A recognisable marker rather than
    /// an empty string, so a row whose key was destroyed is distinguishable from
    /// one that failed to store a key.
    /// </summary>
    public const string DeletedCiphertextMarker = "(deleted)";

    /// <summary>
    /// Normalises a protector GUID so <c>{GUID}</c> and <c>GUID</c> cannot create
    /// two "different" escrows for the same protector and defeat the unique index.
    /// </summary>
    private static string NormalizeProtectorId(string keyProtectorId)
    {
        var value = Guard.NotNullOrWhiteSpace(keyProtectorId, nameof(keyProtectorId), maxLength: 64)
            .Trim()
            .Trim('{', '}');

        if (!Guid.TryParse(value, out var parsed))
        {
            throw new ArgumentException(
                "The key protector id must be a GUID.", nameof(keyProtectorId));
        }

        return parsed.ToString("D");
    }
}
