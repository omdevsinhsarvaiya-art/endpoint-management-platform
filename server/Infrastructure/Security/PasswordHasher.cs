using System.Security.Cryptography;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// PBKDF2 password hashing for platform administrator accounts.
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2-HMAC-SHA256, 600,000 iterations (OWASP's current recommendation for
/// this construction), 128-bit random salt, 256-bit output, via the framework's
/// <see cref="Rfc2898DeriveBytes.Pbkdf2(byte[], byte[], int, HashAlgorithmName, int)"/>
/// primitive — no hand-rolled crypto, only standard composition. Encoded as
/// <c>pbkdf2-sha256$iterations$salt$hash</c> (base64), so the parameters travel
/// with the hash and can be raised later without invalidating existing records:
/// verification reads the encoded parameters, and callers can rehash-on-login
/// when <see cref="NeedsRehash"/> says so.
/// </para>
/// <para>
/// This type is for human passwords only. Machine credentials (enrollment tokens,
/// agent credentials) are 256-bit CSPRNG values and use plain SHA-256 lookup
/// hashing in <see cref="SecretGenerator"/> — do not swap one for the other in
/// either direction.
/// </para>
/// </remarks>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const char Separator = '$';

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return string.Join(
            Separator,
            Scheme,
            Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Verifies a password against an encoded hash. Unknown schemes and malformed
    /// encodings verify as false — never as an exception a caller might map to
    /// something other than "sign-in failed".
    /// </summary>
    public static bool Verify(string password, string encodedHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrEmpty(encodedHash);

        var parts = encodedHash.Split(Separator);

        if (parts.Length != 4 || parts[0] != Scheme)
        {
            return false;
        }

        if (!int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 1 or > 10_000_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True when the stored hash uses weaker parameters than current policy.</summary>
    public static bool NeedsRehash(string encodedHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(encodedHash);

        var parts = encodedHash.Split(Separator);

        return parts.Length != 4
               || parts[0] != Scheme
               || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var iterations)
               || iterations < Iterations;
    }
}
