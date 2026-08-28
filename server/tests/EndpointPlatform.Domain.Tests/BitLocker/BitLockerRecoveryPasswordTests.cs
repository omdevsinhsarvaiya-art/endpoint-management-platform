using EndpointPlatform.Domain.BitLocker;

namespace EndpointPlatform.Domain.Tests.BitLocker;

/// <summary>
/// Validating a BitLocker recovery password.
///
/// The failure mode this guards against is silent: a mistyped key is escrowed,
/// nobody notices, and it is discovered to be useless on the day a machine will
/// not boot. BitLocker gives every group a checksum precisely so a typo can be
/// caught at entry, and checking it here is the difference between an escrow that
/// works and one that only looks like it does.
///
/// Nothing in these tests, or in the type under test, echoes a candidate value
/// back — an error message that quoted the first group would put key material into
/// an API response and a support ticket.
/// </summary>
public sealed class BitLockerRecoveryPasswordTests
{
    /// <summary>
    /// Groups that are multiples of 11 with a quotient inside 16 bits. Built
    /// rather than copied from a real machine, so no genuine key is in the repo.
    /// </summary>
    private static string Valid() => string.Join('-', Enumerable.Repeat("011000", 8));

    [Fact]
    public void A_well_formed_password_is_accepted()
    {
        BitLockerRecoveryPassword.Validate(Valid()).ShouldBe(RecoveryPasswordError.None);
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        BitLockerRecoveryPassword.Validate($"  {Valid()}  ").ShouldBe(RecoveryPasswordError.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_password_is_refused(string? candidate)
    {
        BitLockerRecoveryPassword.Validate(candidate).ShouldBe(RecoveryPasswordError.Empty);
    }

    [Theory]
    [InlineData("011000-011000-011000-011000-011000-011000-011000")]          // 7 groups
    [InlineData("011000-011000-011000-011000-011000-011000-011000-011000-011000")] // 9
    [InlineData("01100-011000-011000-011000-011000-011000-011000-011000")]    // short group
    [InlineData("0110000-011000-011000-011000-011000-011000-011000-011000")]  // long group
    [InlineData("01100a-011000-011000-011000-011000-011000-011000-011000")]   // non-digit
    public void A_malformed_password_is_refused(string candidate)
    {
        BitLockerRecoveryPassword.Validate(candidate).ShouldBe(RecoveryPasswordError.WrongShape);
    }

    /// <summary>
    /// A 48-digit run with no separators is far more likely to be a paste
    /// accident than an intentional entry, and accepting it would silently escrow
    /// something nobody can read back against the key printed on the recovery screen.
    /// </summary>
    [Fact]
    public void A_password_without_separators_is_refused()
    {
        BitLockerRecoveryPassword.Validate(new string('0', 48))
            .ShouldBe(RecoveryPasswordError.WrongShape);
    }

    /// <summary>
    /// The check that catches a single mistyped digit. 011001 is not a multiple
    /// of 11, so BitLocker would never have produced it.
    /// </summary>
    [Fact]
    public void A_group_that_fails_the_checksum_is_refused()
    {
        var candidate = "011001-" + string.Join('-', Enumerable.Repeat("011000", 7));

        BitLockerRecoveryPassword.Validate(candidate).ShouldBe(RecoveryPasswordError.FailedChecksum);
    }

    [Fact]
    public void A_group_whose_quotient_exceeds_sixteen_bits_is_refused()
    {
        // 999999 is not a multiple of 11; 730awkward values aside, use a multiple
        // of 11 that is too large: 725middle. 65536 * 11 = 720896.
        var tooLarge = (65536 * 11).ToString("D6");
        var candidate = tooLarge + "-" + string.Join('-', Enumerable.Repeat("011000", 7));

        BitLockerRecoveryPassword.Validate(candidate).ShouldBe(RecoveryPasswordError.FailedChecksum);
    }

    /// <summary>
    /// Every message is safe to hand back to a caller and to write into a log.
    /// </summary>
    [Fact]
    public void No_error_message_echoes_the_candidate()
    {
        var candidate = "011001-" + string.Join('-', Enumerable.Repeat("011000", 7));
        var message = BitLockerRecoveryPassword.Describe(BitLockerRecoveryPassword.Validate(candidate));

        message.ShouldNotBeNullOrWhiteSpace();
        message.ShouldNotContain("011001");
        message.ShouldNotContain("011000");
        System.Text.RegularExpressions.Regex.IsMatch(message, @"\d{6}").ShouldBeFalse();
    }

    [Fact]
    public void Every_error_has_a_description()
    {
        foreach (var error in Enum.GetValues<RecoveryPasswordError>())
        {
            BitLockerRecoveryPassword.Describe(error).ShouldNotBeNullOrWhiteSpace();
        }
    }
}
