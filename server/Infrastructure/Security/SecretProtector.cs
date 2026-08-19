using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>Seals and unseals short-lived secrets held outside the database.</summary>
public interface ISecretProtector
{
    /// <summary>Returns an opaque sealed form of <paramref name="plaintext"/>.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>. Throws <see cref="CryptographicException"/> if tampered.</summary>
    string Unprotect(string sealedValue);
}

public sealed class SecretProtectionOptions
{
    public const string SectionName = "SecretProtection";

    /// <summary>
    /// Base64 32-byte key for sealing ephemeral secrets. Supplied by configuration or
    /// a secret store; never committed. If absent, a process-local key is generated,
    /// which is fine for a single-node dev run but means in-flight secrets do not
    /// survive a restart or work across instances.
    /// </summary>
    [Required(AllowEmptyStrings = true)]
    public string Key { get; init; } = string.Empty;
}

/// <summary>
/// AES-GCM protection for ephemeral secrets, so a Redis snapshot alone never yields
/// a plaintext password.
/// </summary>
/// <remarks>
/// AES-GCM is authenticated: tampering with the ciphertext fails the tag check and
/// throws rather than returning corrupted plaintext. A fresh random nonce is used per
/// value and stored alongside it, which is required for GCM safety.
/// </remarks>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceLength = 12; // 96-bit nonce, the AES-GCM standard.
    private const int TagLength = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.Key;

        if (string.IsNullOrWhiteSpace(configured))
        {
            // Ephemeral by nature: a lost key only invalidates in-flight secrets, which
            // fail their task safely and can be re-issued.
            _key = RandomNumberGenerator.GetBytes(32);
            return;
        }

        _key = Convert.FromBase64String(configured);

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{SecretProtectionOptions.SectionName}:Key must be a base64-encoded 32-byte key.");
        }
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(_key, TagLength))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        CryptographicOperations.ZeroMemory(plaintextBytes);

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
            throw new CryptographicException("Sealed secret is malformed.", ex);
        }

        if (envelope.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("Sealed secret is truncated.");
        }

        var nonce = envelope.AsSpan(0, NonceLength);
        var tag = envelope.AsSpan(NonceLength, TagLength);
        var ciphertext = envelope.AsSpan(NonceLength + TagLength);
        var plaintextBytes = new byte[ciphertext.Length];

        using (var aes = new AesGcm(_key, TagLength))
        {
            // Throws CryptographicException on a failed tag check (tampering).
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }

        var plaintext = Encoding.UTF8.GetString(plaintextBytes);
        CryptographicOperations.ZeroMemory(plaintextBytes);
        return plaintext;
    }
}
