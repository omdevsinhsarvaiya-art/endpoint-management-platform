using System.Security.Cryptography;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Security;

/// <summary>
/// The two centralized controls this feature rests on: the escrow key protector
/// and the audit redactor.
///
/// Both are the kind of control that is trivially correct until the day it is
/// not, so the tests here are deliberately about the failure modes rather than
/// the happy path — a missing key that silently generates one, and a redactor
/// that lets a secret through because of how it was named.
/// </summary>
public sealed class RecoveryKeyProtectorTests
{
    private static string Key32() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmRecoveryKeyProtector Protector(string? key = null, int version = 1) =>
        new(Options.Create(new RecoveryEscrowOptions { Key = key ?? Key32(), KeyVersion = version }));

    private const string Sample = "011000-011000-011000-011000-011000-011000-011000-011000";

    [Fact]
    public void A_sealed_value_round_trips()
    {
        var protector = Protector();
        var sealedValue = protector.Protect(Sample);

        protector.Unprotect(sealedValue).ShouldBe(Sample);
    }

    /// <summary>
    /// The ciphertext must not contain the plaintext. Obvious, and exactly the
    /// kind of thing that silently regresses if someone "simplifies" the envelope.
    /// </summary>
    [Fact]
    public void The_sealed_value_does_not_contain_the_plaintext()
    {
        var sealedValue = Protector().Protect(Sample);

        sealedValue.ShouldNotContain(Sample);
        sealedValue.ShouldNotContain("011000");
        System.Text.RegularExpressions.Regex.IsMatch(sealedValue, @"\d{6}-\d{6}").ShouldBeFalse();
    }

    /// <summary>Fresh nonce per value: two seals of the same input must differ.</summary>
    [Fact]
    public void Sealing_the_same_value_twice_produces_different_ciphertext()
    {
        var protector = Protector();

        protector.Protect(Sample).ShouldNotBe(protector.Protect(Sample));
    }

    /// <summary>AES-GCM is authenticated: a flipped byte must fail, not decrypt.</summary>
    [Fact]
    public void A_tampered_envelope_is_refused_rather_than_returning_corrupt_plaintext()
    {
        var protector = Protector();
        var bytes = Convert.FromBase64String(protector.Protect(Sample));

        bytes[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void A_value_sealed_with_a_different_key_cannot_be_unsealed()
    {
        var sealedValue = Protector().Protect(Sample);

        Should.Throw<CryptographicException>(() => Protector().Unprotect(sealedValue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_key_stops_the_process_rather_than_generating_one(string key)
    {
        // The whole point of J-1. The ephemeral protector generates a key when
        // none is configured, which is right for Redis and catastrophic here: a
        // process-local key would make every escrowed password unreadable after a
        // restart, discovered only when a key was needed.
        var ex = Should.Throw<InvalidOperationException>(() => Protector(key));

        ex.Message.ShouldContain("RecoveryEscrow:Key");
        ex.Message.ShouldContain("no generated fallback");
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")] // valid base64, wrong length
    public void A_malformed_key_stops_the_process(string key)
    {
        Should.Throw<InvalidOperationException>(() => Protector(key));
    }

    [Fact]
    public void The_key_version_is_reported_so_rows_can_be_re_sealed_later()
    {
        Protector(version: 7).CurrentKeyVersion.ShouldBe(7);
    }

    /// <summary>An unseal failure must not name what it was unsealing.</summary>
    [Fact]
    public void A_failure_message_never_contains_key_material()
    {
        var protector = Protector();
        var ex = Should.Throw<CryptographicException>(() => protector.Unprotect("not-an-envelope"));

        ex.Message.ShouldNotContain(Sample);
        System.Text.RegularExpressions.Regex.IsMatch(ex.Message, @"\d{6}-\d{6}").ShouldBeFalse();
    }
}

/// <summary>
/// The audit redactor, which <c>AuditLogEntry</c> has documented as the supported
/// way to build its state columns since the audit trail was written and which did
/// not exist until now.
///
/// It matters more than an ordinary sanitiser because the audit trail is
/// append-only and enforced by database triggers: a secret written into a row
/// cannot be edited out afterwards.
/// </summary>
public sealed class AuditStateRedactorTests
{
    private const string RecoveryPassword = "011000-011000-011000-011000-011000-011000-011000-011000";

    [Fact]
    public void Ordinary_facts_survive_unchanged()
    {
        var json = AuditStateRedactor.Redact(new { escrowId = "abc", keyVersion = 3, driveLetter = "C:" });

        json.ShouldNotBeNull();
        json!.ShouldContain("abc");
        json.ShouldContain("C:");
        json.ShouldNotContain(AuditStateRedactor.Placeholder);
    }

    /// <summary>Redacted by NAME, whatever the value happens to be.</summary>
    [Theory]
    [InlineData("password")]
    [InlineData("recoveryPassword")]
    [InlineData("recoveryKey")]
    [InlineData("sealedRecoveryPassword")]
    [InlineData("secret")]
    [InlineData("token")]
    public void A_property_named_as_a_secret_is_redacted(string name)
    {
        var json = AuditStateRedactor.Redact(
            new Dictionary<string, object?> { [name] = "something-sensitive" });

        json.ShouldNotBeNull();
        json!.ShouldNotContain("something-sensitive");
        json.ShouldContain(AuditStateRedactor.Placeholder);
    }

    /// <summary>
    /// Redacted by VALUE, whatever the property is called. This is the case a
    /// name-based rule alone misses: a key pasted into a justification field.
    /// </summary>
    [Fact]
    public void A_secret_shaped_value_is_redacted_even_under_a_harmless_name()
    {
        var json = AuditStateRedactor.Redact(new { justification = RecoveryPassword });

        json.ShouldNotBeNull();
        json!.ShouldNotContain("011000");
        json.ShouldContain(AuditStateRedactor.Placeholder);
        AuditStateRedactor.ContainsSecretShape(json).ShouldBeFalse();
    }

    /// <summary>
    /// The false positive that caused a spurious C9 failure in the M13 acceptance
    /// script. This boolean reports that a protector exists and is exactly the
    /// kind of fact the audit trail should keep; redacting it would destroy
    /// information for no gain.
    /// </summary>
    [Fact]
    public void A_property_merely_containing_a_secret_word_is_kept()
    {
        var json = AuditStateRedactor.Redact(new { hasRecoveryPasswordProtector = true });

        json.ShouldNotBeNull();
        json!.ShouldContain("hasRecoveryPasswordProtector");
        json.ShouldContain("true");
        json.ShouldNotContain(AuditStateRedactor.Placeholder);
    }

    [Fact]
    public void Nested_objects_and_arrays_are_scrubbed_too()
    {
        var json = AuditStateRedactor.Redact(new
        {
            outer = new { inner = new { password = "hunter2" } },
            items = new[] { new { note = RecoveryPassword } },
        });

        json.ShouldNotBeNull();
        json!.ShouldNotContain("hunter2");
        json.ShouldNotContain("011000");
    }

    [Fact]
    public void Null_state_stays_null()
    {
        AuditStateRedactor.Redact((object?)null).ShouldBeNull();
    }

    [Fact]
    public void The_shape_detector_recognises_a_recovery_password()
    {
        AuditStateRedactor.ContainsSecretShape($"{{\"x\":\"{RecoveryPassword}\"}}").ShouldBeTrue();
        AuditStateRedactor.ContainsSecretShape("{\"x\":\"nothing here\"}").ShouldBeFalse();
        AuditStateRedactor.ContainsSecretShape(null).ShouldBeFalse();
    }
}
