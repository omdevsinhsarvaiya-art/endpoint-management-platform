using System.Security.Cryptography;
using EndpointAgent.Core.BitLocker;

namespace EndpointAgent.Core.Tests.BitLocker;

/// <summary>
/// Sealing on the endpoint, and the pin that decides which key it seals to.
/// </summary>
/// <remarks>
/// The property being protected is that no server process can read an automatic
/// escrow. That holds only if the agent seals to the intended public key and
/// refuses every other, so the refusals get as much attention here as the happy
/// path.
/// </remarks>
public sealed class RecoveryPasswordSealerTests
{
    private const string Password = "011000-011000-011000-011000-011000-011000-011000-011000";

    [Fact]
    public void A_sealed_envelope_names_its_scheme_and_the_key_it_was_sealed_to()
    {
        using var key = RSA.Create(3072);
        var fingerprint = RecoveryPasswordSealer.Fingerprint(key);

        var envelope = RecoveryPasswordSealer.Seal(Password, key, fingerprint);

        envelope.Scheme.ShouldBe(RecoveryEscrowEnvelope.HybridRsaV1);
        envelope.KeyFingerprint.ShouldBe(fingerprint);
    }

    /// <summary>
    /// A fresh data key and nonce per record: two seals of the same password must
    /// not produce the same bytes, or the ciphertext would leak equality.
    /// </summary>
    [Fact]
    public void Sealing_the_same_password_twice_produces_different_envelopes()
    {
        using var key = RSA.Create(3072);
        var fingerprint = RecoveryPasswordSealer.Fingerprint(key);

        var first = RecoveryPasswordSealer.Seal(Password, key, fingerprint);
        var second = RecoveryPasswordSealer.Seal(Password, key, fingerprint);

        first.Ciphertext.ShouldNotBe(second.Ciphertext);
        first.Nonce.ShouldNotBe(second.Nonce);
        first.WrappedKey.ShouldNotBe(second.WrappedKey);
    }

    [Fact]
    public void The_envelope_contains_nothing_of_the_password()
    {
        using var key = RSA.Create(3072);

        var json = RecoveryPasswordSealer
            .Seal(Password, key, RecoveryPasswordSealer.Fingerprint(key))
            .ToJson();

        json.ShouldNotContain(Password);
        json.ShouldNotContain("011000");
        json.ShouldNotMatch(@"\d{6}-\d{6}");
    }

    /// <summary>
    /// The pin is the control. Without this check an impersonated server could
    /// hand over its own key and receive every recovery password sealed to it.
    /// </summary>
    [Fact]
    public void Sealing_to_a_key_that_fails_the_pin_is_refused()
    {
        using var pinned = RSA.Create(3072);
        using var attacker = RSA.Create(3072);

        var refusal = Should.Throw<InvalidOperationException>(
            () => RecoveryPasswordSealer.Seal(
                Password, attacker, RecoveryPasswordSealer.Fingerprint(pinned)));

        refusal.Message.ShouldContain("fingerprint");
        refusal.Message.ShouldNotContain(Password);
        refusal.Message.ShouldNotContain("011000");
    }

    /// <summary>
    /// A weaker key is refused outright rather than accepted with a warning: an
    /// attacker who can choose the modulus would otherwise choose a breakable one.
    /// </summary>
    [Fact]
    public void A_key_weaker_than_the_minimum_is_refused()
    {
        using var weak = RSA.Create(2048);

        var refusal = Should.Throw<InvalidOperationException>(
            () => RecoveryPasswordSealer.Seal(
                Password, weak, RecoveryPasswordSealer.Fingerprint(weak)));

        refusal.Message.ShouldContain("3072");
        refusal.Message.ShouldNotContain(Password);
    }

    /// <summary>Fingerprints identify a key, so two keys must not share one.</summary>
    [Fact]
    public void Different_keys_have_different_fingerprints()
    {
        using var first = RSA.Create(3072);
        using var second = RSA.Create(3072);

        RecoveryPasswordSealer.Fingerprint(first)
            .ShouldNotBe(RecoveryPasswordSealer.Fingerprint(second));
    }

    [Fact]
    public void A_fingerprint_is_hex_sha256_of_the_spki()
    {
        using var key = RSA.Create(3072);

        var expected = Convert.ToHexString(
            SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        RecoveryPasswordSealer.Fingerprint(key).ShouldBe(expected);
        RecoveryPasswordSealer.Fingerprint(key).Length.ShouldBe(64);
    }

    /// <summary>Case and whitespace in a stored pin must not cause a false refusal.</summary>
    [Fact]
    public void A_pin_is_compared_case_insensitively_and_trimmed()
    {
        using var key = RSA.Create(3072);
        var noisy = "  " + RecoveryPasswordSealer.Fingerprint(key).ToUpperInvariant() + "  ";

        Should.NotThrow(() => RecoveryPasswordSealer.Seal(Password, key, noisy));
    }

    /// <summary>
    /// Tampering must fail closed. Without this the server could be handed a
    /// modified envelope and would have no way to notice at reveal time.
    /// </summary>
    [Fact]
    public void A_tampered_envelope_fails_its_authentication_tag()
    {
        using var key = RSA.Create(3072);
        var envelope = RecoveryPasswordSealer.Seal(Password, key, RecoveryPasswordSealer.Fingerprint(key));

        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        ciphertext[0] ^= 0xFF;

        var dataKey = key.Decrypt(
            Convert.FromBase64String(envelope.WrappedKey), RSAEncryptionPadding.OaepSHA256);

        using var aes = new AesGcm(dataKey, 16);

        Should.Throw<CryptographicException>(() => aes.Decrypt(
            Convert.FromBase64String(envelope.Nonce),
            ciphertext,
            Convert.FromBase64String(envelope.Tag),
            new byte[ciphertext.Length]));
    }
}

/// <summary>
/// The endpoint's copy of the recovery-password rule.
/// </summary>
/// <remarks>
/// It must agree exactly with the server's <c>BitLockerRecoveryPassword</c>. The
/// two are stated separately because the agent cannot reference the server's domain
/// assembly, and a drift between them would mean either refusing valid keys or --
/// worse, since the server never opens an automatic envelope -- sealing invalid
/// ones that nobody discovers until they are needed.
/// </remarks>
public sealed class RecoveryPasswordFormatTests
{
    [Fact]
    public void A_well_formed_password_is_accepted()
    {
        RecoveryPasswordFormat
            .IsWellFormed("011000-011000-011000-011000-011000-011000-011000-011000")
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, RecoveryPasswordFormatError.Empty)]
    [InlineData("", RecoveryPasswordFormatError.Empty)]
    [InlineData("   ", RecoveryPasswordFormatError.Empty)]
    [InlineData("011000", RecoveryPasswordFormatError.WrongShape)]
    [InlineData("011000-011000-011000-011000-011000-011000-011000", RecoveryPasswordFormatError.WrongShape)]
    [InlineData("01100a-011000-011000-011000-011000-011000-011000-011000", RecoveryPasswordFormatError.WrongShape)]
    [InlineData("11000-011000-011000-011000-011000-011000-011000-011000", RecoveryPasswordFormatError.WrongShape)]
    public void Malformed_candidates_are_rejected(string? candidate, RecoveryPasswordFormatError expected)
    {
        RecoveryPasswordFormat.Validate(candidate).ShouldBe(expected);
    }

    /// <summary>
    /// The checksum is what separates a real key from 48 plausible digits, and it
    /// is the reason a mistyped or invented value is caught before it is sealed.
    /// </summary>
    [Fact]
    public void A_group_failing_the_checksum_is_rejected()
    {
        RecoveryPasswordFormat
            .Validate("011001-011000-011000-011000-011000-011000-011000-011000")
            .ShouldBe(RecoveryPasswordFormatError.FailedChecksum);
    }

    [Fact]
    public void An_unseparated_run_of_48_digits_is_not_accepted()
    {
        RecoveryPasswordFormat.IsWellFormed(new string('0', 48)).ShouldBeFalse();
    }

    /// <summary>Surrounding whitespace is tolerated; a value is not rejected for it.</summary>
    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        RecoveryPasswordFormat
            .IsWellFormed("  011000-011000-011000-011000-011000-011000-011000-011000  ")
            .ShouldBeTrue();
    }
}
