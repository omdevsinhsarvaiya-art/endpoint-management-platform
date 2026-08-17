using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Enrollment;

/// <summary>
/// The long-term credential a device uses to authenticate to the Agent API.
/// </summary>
/// <remarks>
/// <para>
/// Issued at enrollment: the server generates a 256-bit secret, returns it to the
/// agent exactly once over TLS, and stores only this record — device id, key id
/// and a SHA-256 hash of the secret. The agent stores the secret DPAPI-protected
/// on the endpoint. Authentication presents <c>keyId</c> + secret; the server
/// hashes the presented secret and compares against <see cref="SecretHash"/> in
/// constant time.
/// </para>
/// <para>
/// One ACTIVE credential per device, but multiple rows over time: rotation issues
/// a new credential and revokes the old, and the history of revoked credentials is
/// itself audit-relevant (a burst of rotations on one device is a signal).
/// </para>
/// <para>
/// Why an opaque bearer secret rather than mTLS client certificates for this
/// phase: the security properties that matter here (per-device identity, server-side
/// revocation, no fleet-wide secret, hash-at-rest) are identical, while avoiding
/// certificate lifecycle machinery (CA, CRL/OCSP, renewal windows) that none of the
/// current requirements need. The credential is carried in a header over TLS.
/// Moving to mTLS later changes the transport handshake, not this model: the
/// row becomes certificate metadata (thumbprint instead of secret hash). See
/// docs/adr/0008-agent-authentication.md.
/// </para>
/// </remarks>
public sealed class AgentCredential : AuditableEntity
{
    public const int SecretHashLength = 64; // hex-encoded SHA-256
    public const int KeyIdLength = 32;      // hex-encoded 128-bit

    private AgentCredential()
    {
        KeyId = null!;
        SecretHash = null!;
    }

    public AgentCredential(Guid deviceId, string keyId, string secretHash, DateTimeOffset issuedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        KeyId = ValidateHex(keyId, KeyIdLength, nameof(keyId));
        SecretHash = ValidateHex(secretHash, SecretHashLength, nameof(secretHash));
        IssuedAt = issuedAt;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>
    /// Public identifier presented alongside the secret. Lets the server look the
    /// credential up without indexing on the secret hash, and names the credential
    /// in audit entries without ever touching the secret.
    /// </summary>
    public string KeyId { get; private set; }

    /// <summary>Hex-encoded SHA-256 of the credential secret. The secret is never stored.</summary>
    public string SecretHash { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Set on every successful authentication; stale values reveal orphaned credentials.</summary>
    public DateTimeOffset? LastUsedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void RecordUse(DateTimeOffset now) => LastUsedAt = now;

    private static string ValidateHex(string value, int requiredLength, string paramName)
    {
        var validated = Guard.NotNullOrWhiteSpace(value, paramName, maxLength: requiredLength);

        if (validated.Length != requiredLength)
        {
            throw new ArgumentException(
                $"Value must be exactly {requiredLength} lowercase hex characters.", paramName);
        }

        foreach (var c in validated)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                throw new ArgumentException("Value must be lowercase hexadecimal.", paramName);
            }
        }

        return validated;
    }
}
