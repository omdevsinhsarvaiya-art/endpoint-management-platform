using System.Globalization;

namespace EndpointPlatform.Domain.BitLocker;

/// <summary>Why a recovery password was refused.</summary>
public enum RecoveryPasswordError
{
    None = 0,
    Empty = 1,
    WrongShape = 2,
    FailedChecksum = 3,
}

/// <summary>
/// Validates a BitLocker recovery password without ever storing or logging one.
/// </summary>
/// <remarks>
/// <para>
/// A recovery password is 48 digits in eight groups of six. The groups are not
/// arbitrary: each is a multiple of 11, and each divided by 11 fits in 16 bits.
/// Checking both catches a mistyped or invented key before it is sealed and
/// stored, which matters because the failure mode of a bad escrow is silent --
/// nobody discovers it until the day the key is needed and does not work.
/// </para>
/// <para>
/// <b>Nothing in this type retains, returns or renders the password.</b> It takes
/// a string and answers a yes/no with a reason; the reason names the rule that
/// failed, never the value that failed it. Validation is deliberately server-side:
/// the client's copy of this is a convenience, not a control.
/// </para>
/// </remarks>
public static class BitLockerRecoveryPassword
{
    public const int GroupCount = 8;
    public const int GroupLength = 6;

    /// <summary>Each group is a multiple of this.</summary>
    private const int Divisor = 11;

    /// <summary>Each group divided by <see cref="Divisor"/> must fit in 16 bits.</summary>
    private const int MaxQuotient = 65535;

    /// <summary>
    /// Whether <paramref name="candidate"/> is a well-formed recovery password.
    /// </summary>
    /// <remarks>
    /// Accepts the canonical hyphenated form and tolerates surrounding whitespace.
    /// It does not tolerate a missing separator: a 48-digit run is far more likely
    /// to be a paste accident than an intentional entry, and accepting it would
    /// silently escrow a key nobody can read back.
    /// </remarks>
    public static RecoveryPasswordError Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return RecoveryPasswordError.Empty;
        }

        var groups = candidate.Trim().Split('-');

        if (groups.Length != GroupCount)
        {
            return RecoveryPasswordError.WrongShape;
        }

        foreach (var group in groups)
        {
            if (group.Length != GroupLength || !group.All(char.IsAsciiDigit))
            {
                return RecoveryPasswordError.WrongShape;
            }

            // Parsed as int: six digits cannot overflow, so a parse failure here
            // would mean the digit check above is wrong rather than bad input.
            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return RecoveryPasswordError.WrongShape;
            }

            if (value % Divisor != 0 || value / Divisor > MaxQuotient)
            {
                return RecoveryPasswordError.FailedChecksum;
            }
        }

        return RecoveryPasswordError.None;
    }

    /// <summary>
    /// A message safe to return to a caller.
    /// </summary>
    /// <remarks>
    /// Never includes the candidate, not even partially. A validation error that
    /// echoed the first group back would put key material into an API response,
    /// a browser console and quite possibly a support ticket.
    /// </remarks>
    public static string Describe(RecoveryPasswordError error) => error switch
    {
        RecoveryPasswordError.None => "The recovery password is well formed.",
        RecoveryPasswordError.Empty => "A recovery password is required.",
        RecoveryPasswordError.WrongShape =>
            "A BitLocker recovery password is 48 digits in eight hyphen-separated groups of six.",
        RecoveryPasswordError.FailedChecksum =>
            "That is not a valid BitLocker recovery password: one or more groups failed the "
            + "checksum BitLocker applies to every group. Check for a mistyped digit.",
        _ => "The recovery password could not be validated.",
    };
}
