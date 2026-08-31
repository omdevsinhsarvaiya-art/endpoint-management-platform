namespace EndpointPlatform.Domain.BitLocker;

/// <summary>
/// Who filed an escrowed recovery password.
/// </summary>
/// <remarks>
/// Worth distinguishing rather than inferring from a null user id, because the two
/// carry different trust. A manual escrow was read off a screen and typed by a
/// named administrator who vouched for it. An automatic escrow was read from
/// Windows by the endpoint itself and sealed there; nobody looked at it, and the
/// platform is trusting the agent's assertion that the value belongs to the
/// protector it names. Both are legitimate, but an operator comparing two records
/// for the same machine should be able to see which is which.
/// </remarks>
public enum BitLockerEscrowOrigin
{
    /// <summary>Typed into the console by an administrator. The original model.</summary>
    Manual = 0,

    /// <summary>Collected and sealed on the endpoint, then uploaded by the agent.</summary>
    Automatic = 1,
}

/// <summary>
/// How a stored recovery password was sealed.
/// </summary>
/// <remarks>
/// <para>
/// Recorded per row rather than assumed, because two schemes now coexist and a
/// reader cannot tell them apart from the ciphertext alone. Manual escrow seals
/// with the Admin API's symmetric master key; automatic escrow is sealed on the
/// endpoint under a public key whose private half only the Admin API holds, so the
/// plaintext never exists on the server during ingestion.
/// </para>
/// <para>
/// Stored as text rather than an ordinal so a row remains self-describing in a
/// database dump, and so adding a scheme later cannot renumber the existing ones.
/// </para>
/// </remarks>
public static class BitLockerSealScheme
{
    /// <summary>
    /// AES-GCM under <c>RecoveryEscrow:Key</c>. Every row written before automatic
    /// escrow existed is this, which is why it is the column default.
    /// </summary>
    public const string AesGcmV1 = "aesgcm-v1";

    /// <summary>
    /// Sealed on the endpoint: AES-256-GCM under a per-record data key, that key
    /// wrapped with RSA-3072-OAEP. Only the Admin API can unwrap it.
    /// </summary>
    public const string HybridRsaV1 = "hybrid-rsa-v1";

    public const int MaxLength = 32;

    public static bool IsKnown(string? scheme) =>
        scheme is AesGcmV1 or HybridRsaV1;
}
