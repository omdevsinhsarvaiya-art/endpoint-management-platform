using System.Globalization;

namespace EndpointAgent.Core.BitLocker;

/// <summary>Why a recovery password was rejected on the endpoint.</summary>
public enum RecoveryPasswordFormatError
{
    None = 0,
    Empty = 1,
    WrongShape = 2,
    FailedChecksum = 3,
}

/// <summary>
/// Validates a BitLocker recovery password on the endpoint, before it is sealed.
/// </summary>
/// <remarks>
/// <para>
/// A deliberate mirror of the server's <c>BitLockerRecoveryPassword</c>, not a
/// shared reference: the agent cannot see the server's domain assembly, and giving
/// it one to import a validator would be a poor trade. The two must agree, so the
/// rule is stated identically in both and asserted by tests on both sides.
/// </para>
/// <para>
/// This check matters more here than it used to on the server. Automatic escrow
/// seals on the endpoint and the server never opens the envelope during ingestion,
/// so this is the <em>only</em> point at which anything verifies that what Windows
/// returned is a recovery password at all. If it passes here and is wrong, nobody
/// finds out until the key is needed.
/// </para>
/// <para>
/// <b>Nothing here retains, returns or logs the candidate.</b> It answers with a
/// reason naming the rule that failed, never the value that failed it.
/// </para>
/// </remarks>
public static class RecoveryPasswordFormat
{
    public const int GroupCount = 8;
    public const int GroupLength = 6;

    private const int Divisor = 11;
    private const int MaxQuotient = 65535;

    /// <summary>
    /// Whether <paramref name="candidate"/> is a well-formed recovery password:
    /// eight hyphen-separated groups of six digits, each a multiple of eleven whose
    /// quotient fits in sixteen bits.
    /// </summary>
    public static RecoveryPasswordFormatError Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return RecoveryPasswordFormatError.Empty;
        }

        var groups = candidate.Trim().Split('-');

        if (groups.Length != GroupCount)
        {
            return RecoveryPasswordFormatError.WrongShape;
        }

        foreach (var group in groups)
        {
            if (group.Length != GroupLength || !group.All(char.IsAsciiDigit))
            {
                return RecoveryPasswordFormatError.WrongShape;
            }

            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return RecoveryPasswordFormatError.WrongShape;
            }

            if (value % Divisor != 0 || value / Divisor > MaxQuotient)
            {
                return RecoveryPasswordFormatError.FailedChecksum;
            }
        }

        return RecoveryPasswordFormatError.None;
    }

    public static bool IsWellFormed(string? candidate) =>
        Validate(candidate) == RecoveryPasswordFormatError.None;
}
