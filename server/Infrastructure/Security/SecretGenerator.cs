using System.Security.Cryptography;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Generates and hashes the platform's machine credentials (enrollment token
/// secrets and agent credential secrets).
/// </summary>
/// <remarks>
/// <para>
/// These are 256-bit CSPRNG values, which is why plain SHA-256 (no salt, no
/// stretching) is the right hash at rest: unlike passwords there is no
/// low-entropy input to dictionary-attack, and equality lookup by hash must be
/// possible. Human passwords (Phase 3) use a dedicated password hasher instead —
/// do not reuse this type for them.
/// </para>
/// <para>
/// Secrets are lowercase hex rather than base64: no padding, no URL-unsafe
/// characters, trivially selectable by double-click, and identical alphabet to
/// the stored hashes.
/// </para>
/// </remarks>
public static class SecretGenerator
{
    /// <summary>256-bit secret as 64 lowercase hex characters.</summary>
    public static string GenerateSecret() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>128-bit key id as 32 lowercase hex characters.</summary>
    public static string GenerateKeyId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Hex-encoded SHA-256 of a secret, for storage and lookup.</summary>
    public static string HashSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>
    /// Constant-time comparison of two hex hashes. The hash inputs here are not
    /// secret, but comparing in constant time costs nothing and removes the
    /// entire timing-analysis conversation.
    /// </summary>
    public static bool HashesEqual(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
