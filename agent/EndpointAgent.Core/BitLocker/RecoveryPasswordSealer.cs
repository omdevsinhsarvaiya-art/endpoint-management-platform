using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndpointAgent.Core.BitLocker;

/// <summary>
/// The sealed form of a recovery password, as it travels and as it is stored.
/// </summary>
/// <remarks>
/// Every field is ciphertext or a public parameter. There is no member a plaintext
/// password could occupy, which is what makes it safe for this type to be
/// serialised, logged by accident, or persisted verbatim.
/// </remarks>
/// <param name="Scheme">Names the algorithm set, so the Admin API dispatches rather than guesses.</param>
/// <param name="WrappedKey">The per-record AES key, RSA-3072-OAEP encrypted. Base64.</param>
/// <param name="Nonce">AES-GCM nonce, 12 bytes. Base64.</param>
/// <param name="Tag">AES-GCM authentication tag, 16 bytes. Base64.</param>
/// <param name="Ciphertext">The recovery password under AES-256-GCM. Base64.</param>
/// <param name="KeyFingerprint">
/// Which sealing key this was wrapped to. Lets the server route to the right
/// private key after a rotation without trial decryption, and lets it reject an
/// envelope sealed to a key it does not hold.
/// </param>
public sealed record RecoveryEscrowEnvelope(
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("wrappedKey")] string WrappedKey,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("keyFingerprint")] string KeyFingerprint)
{
    /// <summary>The only scheme this agent produces.</summary>
    public const string HybridRsaV1 = "hybrid-rsa-v1";

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// Seals a recovery password on the endpoint so no server process ever holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why public-key sealing rather than sending the password over TLS.</b> The
/// platform deliberately keeps the escrow decryption key in the Admin API and out
/// of the Agent API, because the Agent API is reachable by every managed endpoint.
/// Automatic escrow originates at the endpoint, so plaintext arriving over the
/// agent channel would land in exactly the process that is not supposed to be able
/// to read it -- and from there into its logs, its exception handlers and its crash
/// dumps. Sealing here means the Agent API receives something it cannot open, and
/// no amount of access to that host yields a recovery password.
/// </para>
/// <para>
/// Hybrid rather than RSA alone: the password is small enough to encrypt directly,
/// but a per-record AES key keeps the construction conventional, keeps envelope
/// size independent of the RSA modulus, and makes a future move to a KMS-held
/// private key a change of unwrap implementation rather than of format.
/// </para>
/// <para>
/// <b>The fingerprint is checked before anything is sealed</b>, and callers are
/// expected to have checked it before the password was even retrieved. A sealer
/// that encrypted to whatever key it was handed would make the pin advisory.
/// </para>
/// </remarks>
public static class RecoveryPasswordSealer
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int DataKeyLength = 32;

    /// <summary>Minimum modulus this agent will seal to. Refuses a downgraded key.</summary>
    public const int MinimumRsaKeySizeBits = 3072;

    /// <summary>
    /// Hex SHA-256 over a public key's SPKI encoding.
    /// </summary>
    /// <remarks>
    /// SPKI rather than the raw modulus so the fingerprint covers the algorithm
    /// identifier too: two keys that differ only in declared algorithm must not
    /// share a fingerprint.
    /// </remarks>
    public static string Fingerprint(RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        var spki = publicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    /// <summary>
    /// Seals <paramref name="recoveryPassword"/> to <paramref name="publicKey"/>.
    /// </summary>
    /// <param name="expectedFingerprint">
    /// The pinned fingerprint. Sealing is refused unless the key matches it.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The key does not match the pin, or is weaker than
    /// <see cref="MinimumRsaKeySizeBits"/>. The message names neither the password
    /// nor any part of it.
    /// </exception>
    public static RecoveryEscrowEnvelope Seal(
        string recoveryPassword,
        RSA publicKey,
        string expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPassword);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);

        if (publicKey.KeySize < MinimumRsaKeySizeBits)
        {
            throw new InvalidOperationException(
                $"The escrow sealing key is {publicKey.KeySize} bits; at least "
                + $"{MinimumRsaKeySizeBits} are required.");
        }

        var actual = Fingerprint(publicKey);

        // Fixed-time so the comparison cannot be probed, though the values are
        // public: the habit matters more than this instance.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expectedFingerprint.Trim().ToLowerInvariant())))
        {
            throw new InvalidOperationException(
                "The escrow sealing key does not match the fingerprint pinned at enrollment. "
                + "No recovery password was sealed.");
        }

        var dataKey = RandomNumberGenerator.GetBytes(DataKeyLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        // The one unavoidable managed copy. See SealBytes for why the caller's
        // string cannot be scrubbed and this array can.
        var plaintext = Encoding.UTF8.GetBytes(recoveryPassword);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        try
        {
            using var aes = new AesGcm(dataKey, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var wrapped = publicKey.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);

            return new RecoveryEscrowEnvelope(
                RecoveryEscrowEnvelope.HybridRsaV1,
                Convert.ToBase64String(wrapped),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(ciphertext),
                actual);
        }
        finally
        {
            // The data key is the thing worth erasing: it is the only value here
            // that, with the ciphertext, reconstitutes the password.
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
