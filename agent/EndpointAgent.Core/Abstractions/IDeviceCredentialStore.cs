namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The device credential issued at enrollment: the machine's identity.
/// </summary>
/// <remarks>
/// Instances live briefly in memory. At rest the credential exists only inside
/// the store implementation (DPAPI-protected on Windows). No member of this type
/// may ever be logged; <see cref="ToString"/> is overridden to enforce that even
/// an accidental structured-logging capture yields nothing.
/// </remarks>
/// <param name="SealingKeyFingerprint">
/// Hex SHA-256 of the escrow sealing key's SPKI, pinned at enrollment.
/// <para>
/// <b>Null means this device may not escrow recovery passwords automatically.</b>
/// Credentials stored before automatic escrow existed deserialize with it null, so
/// every already-enrolled machine is ineligible until it re-enrolls -- which is the
/// intended behaviour, not an upgrade gap. Trust-on-first-use was rejected: an
/// agent that accepted whatever fingerprint arrived first would hand its recovery
/// passwords to anyone able to impersonate the server once.
/// </para>
/// </param>
public sealed record DeviceCredential(
    Guid DeviceId,
    string KeyId,
    string Secret,
    string? SealingKeyFingerprint = null)
{
    /// <summary>The wire form presented in the credential header: <c>keyId.secret</c>.</summary>
    public string ToHeaderValue() => $"{KeyId}.{Secret}";

    /// <summary>
    /// Whether this credential permits automatic recovery-password escrow.
    /// </summary>
    /// <remarks>
    /// Checked before Windows is asked for anything. A device that is not eligible
    /// never reaches the retrieval call at all, so an ineligible machine's recovery
    /// password is not merely unsent -- it is never read.
    /// </remarks>
    public bool IsAutomaticEscrowEligible => !string.IsNullOrWhiteSpace(SealingKeyFingerprint);

    public override string ToString() => $"DeviceCredential(DeviceId: {DeviceId}, KeyId: {KeyId}, Secret: <redacted>)";
}

/// <summary>
/// Secure storage for the long-lived device credential established at enrollment.
/// </summary>
/// <remarks>
/// <para>
/// The credential is what proves this machine's identity to the Agent API for the
/// rest of its life, so its storage is a security boundary in its own right. The
/// Windows implementation protects it with DPAPI at machine scope and writes it to
/// a directory ACL'd to SYSTEM and Administrators, meaning a standard user on the
/// endpoint cannot read it and the blob cannot be decrypted on another machine.
/// </para>
/// <para>
/// Implementations must never log the secret and must treat a corrupt or
/// undecryptable blob as "no credential" (forcing re-enrollment) rather than
/// crashing the service.
/// </para>
/// </remarks>
public interface IDeviceCredentialStore
{
    /// <summary>The stored credential, or null when the machine has never enrolled (or the blob is unreadable).</summary>
    ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the credential, replacing any previous one.</summary>
    ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default);

    /// <summary>True when this machine has a stored credential.</summary>
    ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the stored credential. Used when a device is retired or re-enrolled.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
