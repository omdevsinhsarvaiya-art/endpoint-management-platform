using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Seals and unseals escrowed BitLocker recovery passwords.
/// </summary>
/// <remarks>
/// Separate from <see cref="ISecretProtector"/> on purpose, and the difference is
/// not cosmetic. That one protects <em>ephemeral</em> secrets in Redis and falls
/// back to a process-local key when none is configured, which is correct there: a
/// lost key invalidates in-flight secrets that fail their task safely and can be
/// re-issued. Escrow is the opposite. A key lost here means every escrowed
/// recovery password is permanently undecryptable, and nobody discovers it until
/// the day a machine will not boot.
/// </remarks>
public interface IRecoveryKeyProtector
{
    /// <summary>Which key sealed values produced now, recorded on each row for re-keying.</summary>
    int CurrentKeyVersion { get; }

    /// <summary>Seals a recovery password. The plaintext is zeroed before returning.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>. Throws on tampering or a wrong key.</summary>
    string Unprotect(string sealedValue);
}

public sealed class RecoveryEscrowOptions
{
    public const string SectionName = "RecoveryEscrow";

    /// <summary>
    /// Base64 32-byte key sealing escrowed recovery passwords. Mandatory: there is
    /// deliberately no default and no generated fallback.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Bumped when the key is rotated, and stamped on every row sealed afterwards
    /// so a re-key can find what it still has to convert.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int KeyVersion { get; init; } = 1;
}

/// <summary>
/// AES-GCM protection for escrowed recovery passwords.
/// </summary>
/// <remarks>
/// <para>
/// Envelope layout is nonce (12) || tag (16) || ciphertext, matching the ephemeral
/// protector so one reviewer can read both. A fresh random nonce per value is
/// required for GCM safety and is not optional.
/// </para>
/// <para>
/// <b>The key is mandatory and this type throws at construction without it</b>,
/// which surfaces as a startup failure rather than as an escrow that appears to
/// work and cannot be read back after a restart. Options validation runs on start,
/// so the failure is loud and immediate.
/// </para>
/// <para>
/// <b>Known limitation, accepted for this stage.</b> The key lives in
/// configuration on the host. Anyone holding both a database dump and that host's
/// configuration can decrypt every escrowed password. A KMS or HSM removes that
/// and is deliberately future work; see docs/threat-model.md.
/// </para>
/// <para>
/// Nothing in this type logs, returns or embeds plaintext in an exception. A
/// failure says that unsealing failed, never what was being unsealed.
/// </para>
/// </remarks>
public sealed class AesGcmRecoveryKeyProtector : IRecoveryKeyProtector
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _key;

    public AesGcmRecoveryKeyProtector(IOptions<RecoveryEscrowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.Key;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{RecoveryEscrowOptions.SectionName}:Key is required. Recovery-key escrow seals data at "
                + "rest, so there is deliberately no generated fallback: a process-local key would make "
                + "every escrowed recovery password unreadable after a restart, and the loss would only "
                + "be discovered when a key was needed.");
        }

        try
        {
            _key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{RecoveryEscrowOptions.SectionName}:Key must be a base64-encoded 32-byte key.", ex);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{RecoveryEscrowOptions.SectionName}:Key must decode to exactly 32 bytes "
                + $"(got {_key.Length}).");
        }

        CurrentKeyVersion = options.Value.KeyVersion;
    }

    public int CurrentKeyVersion { get; }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        try
        {
            using var aes = new AesGcm(_key, TagLength);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }
        finally
        {
            // The caller's string is beyond reach, but this copy is not.
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        var envelope = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, NonceLength);
        ciphertext.CopyTo(envelope, NonceLength + TagLength);

        return Convert.ToBase64String(envelope);
    }

    public string Unprotect(string sealedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sealedValue);

        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(sealedValue);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The sealed recovery password is malformed.", ex);
        }

        if (envelope.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("The sealed recovery password is truncated.");
        }

        var nonce = envelope.AsSpan(0, NonceLength);
        var tag = envelope.AsSpan(NonceLength, TagLength);
        var ciphertext = envelope.AsSpan(NonceLength + TagLength);
        var plaintextBytes = new byte[ciphertext.Length];

        // Throws CryptographicException on a failed tag check, which is what a
        // tampered row or the wrong key looks like. The message names neither.
        using (var aes = new AesGcm(_key, TagLength))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }

        try
        {
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
