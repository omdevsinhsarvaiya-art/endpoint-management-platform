using System.Security.Cryptography;
using System.Text;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.BitLocker;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.BitLocker;

/// <summary>
/// The gates that decide whether a recovery password may be read at all.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that matter most in this feature, and what they assert is
/// not "escrow was refused" but <b>"Windows was never asked"</b>. The distinction is
/// the whole point: a password that was read and then discarded has existed in this
/// process's memory, in a managed string that cannot be reliably erased. A password
/// that was never retrieved has not.
/// </para>
/// <para>
/// The reader here therefore records whether it was called, and every blocked case
/// asserts that count is zero rather than merely checking the outcome.
/// </para>
/// </remarks>
public sealed class AutomaticEscrowGateTests
{
    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string Protector = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    /// <summary>Valid: eight groups of six digits, each a multiple of eleven.</summary>
    private const string ValidPassword =
        "011000-011000-011000-011000-011000-011000-011000-011000";

    /// <summary>
    /// Records whether it was asked for a password, which is the assertion these
    /// tests are really making.
    /// </summary>
    private sealed class SpyReader(
        RecoveryPasswordReadResult result) : IRecoveryPasswordReader
    {
        public int Calls { get; private set; }

        public string? RequestedProtector { get; private set; }

        public Task<RecoveryPasswordReadResult> ReadAsync(
            string volumeDeviceIdentifier,
            string keyProtectorId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestedProtector = keyProtectorId;
            return Task.FromResult(result);
        }

        public static SpyReader Returning(string password) =>
            new(new RecoveryPasswordReadResult(RecoveryPasswordReadStatus.Success, password, Protector));

        public static SpyReader Failing(RecoveryPasswordReadStatus status) =>
            new(RecoveryPasswordReadResult.Failed(status, Protector));
    }

    private static AutomaticEscrowGate Gate(IRecoveryPasswordReader reader) =>
        new(reader, NullLogger<AutomaticEscrowGate>.Instance);

    private static RSA NewKey() => RSA.Create(3072);

    private static DeviceCredential Credential(string? fingerprint) =>
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64), fingerprint);

    // ---- the three gates that must prevent retrieval ----------------------

    /// <summary>
    /// A device enrolled before automatic escrow existed has no pinned fingerprint.
    /// It must keep working, and it must not have its recovery password read.
    /// </summary>
    [Fact]
    public async Task Without_a_pinned_fingerprint_the_password_is_never_read()
    {
        var reader = SpyReader.Returning(ValidPassword);
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(fingerprint: null), key, Volume, [Protector], Protector, alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.NotEligible);
        result.Envelope.ShouldBeNull();

        reader.Calls.ShouldBe(0, "an ineligible device must never reach GetKeyProtectorNumericalPassword");
    }

    /// <summary>
    /// The pin is what stops an impersonated server from harvesting keys, so a
    /// mismatch has to block retrieval, not just block the upload.
    /// </summary>
    [Fact]
    public async Task A_fingerprint_mismatch_means_the_password_is_never_read()
    {
        var reader = SpyReader.Returning(ValidPassword);
        using var pinned = NewKey();
        using var offered = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(pinned)),
            offered,
            Volume,
            [Protector],
            Protector,
            alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.FingerprintMismatch);
        result.Envelope.ShouldBeNull();

        reader.Calls.ShouldBe(0, "a key that fails the pin must not lead to a password being read");
    }

    /// <summary>
    /// Idempotence, and a privacy property: a machine whose key is already filed
    /// never materialises that key again on any later inventory pass.
    /// </summary>
    [Fact]
    public async Task An_already_escrowed_protector_is_not_read_again()
    {
        var reader = SpyReader.Returning(ValidPassword);
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key,
            Volume,
            [Protector],
            Protector,
            alreadyEscrowed: true);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.AlreadyEscrowed);
        reader.Calls.ShouldBe(0, "repeat inventory must not re-read a key that is already escrowed");
    }

    /// <summary>A missing key is a mismatch, not a reason to proceed unsealed.</summary>
    [Fact]
    public async Task A_missing_sealing_key_blocks_retrieval()
    {
        var reader = SpyReader.Returning(ValidPassword);

        var result = await Gate(reader).TrySealAsync(
            Credential(new string('c', 64)), null, Volume, [Protector], Protector, alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.FingerprintMismatch);
        reader.Calls.ShouldBe(0);
    }

    // ---- and the path where retrieval is legitimate -----------------------

    [Fact]
    public async Task With_every_gate_passed_the_password_is_read_once_and_sealed()
    {
        var reader = SpyReader.Returning(ValidPassword);
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key,
            Volume,
            [Protector],
            Protector,
            alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.Sealed);
        result.Envelope.ShouldNotBeNull();

        reader.Calls.ShouldBe(1, "exactly one read per escrow, never a retry loop inside the gate");
        reader.RequestedProtector.ShouldBe(Protector);
    }

    /// <summary>
    /// The envelope is what leaves the machine, so it is asserted directly to
    /// carry nothing of the password.
    /// </summary>
    [Fact]
    public async Task The_sealed_envelope_contains_no_trace_of_the_password()
    {
        var reader = SpyReader.Returning(ValidPassword);
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key, Volume, [Protector], Protector, alreadyEscrowed: false);

        var json = result.Envelope!.ToJson();

        json.ShouldNotContain(ValidPassword);
        json.ShouldNotContain("011000");
        json.ShouldNotMatch(@"\d{6}-\d{6}");
    }

    // ---- failure categories, none of which carry a value ------------------

    [Theory]
    [InlineData(RecoveryPasswordReadStatus.Refused, AutomaticEscrowOutcome.WindowsRefused)]
    [InlineData(RecoveryPasswordReadStatus.ProtectorGone, AutomaticEscrowOutcome.ProtectorGone)]
    [InlineData(RecoveryPasswordReadStatus.Malformed, AutomaticEscrowOutcome.MalformedPassword)]
    public async Task A_refused_read_is_reported_as_a_category(
        RecoveryPasswordReadStatus status, AutomaticEscrowOutcome expected)
    {
        var reader = SpyReader.Failing(status);
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key, Volume, [Protector], Protector, alreadyEscrowed: false);

        result.Outcome.ShouldBe(expected);
        result.Envelope.ShouldBeNull();
    }

    /// <summary>
    /// The server never opens the envelope during ingestion, so this is the only
    /// point anything checks that Windows returned a recovery password at all.
    /// </summary>
    [Fact]
    public async Task A_value_that_is_not_a_recovery_password_is_never_sealed()
    {
        var reader = SpyReader.Returning("not-a-recovery-password");
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key, Volume, [Protector], Protector, alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.MalformedPassword);
        result.Envelope.ShouldBeNull();
    }

    /// <summary>
    /// A password filed against the wrong protector is undetectable until the day
    /// it fails to unlock something, so the association is verified rather than
    /// assumed.
    /// </summary>
    [Fact]
    public async Task A_password_for_a_different_protector_is_refused()
    {
        var other = Guid.CreateVersion7().ToString();
        var reader = new SpyReader(
            new RecoveryPasswordReadResult(RecoveryPasswordReadStatus.Success, ValidPassword, other));

        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key, Volume, [Protector], Protector, alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.MalformedPassword);
        result.Envelope.ShouldBeNull();
    }

    /// <summary>Brace style must not look like a protector mismatch.</summary>
    [Fact]
    public async Task Protector_ids_match_across_brace_and_case_differences()
    {
        var reader = new SpyReader(new RecoveryPasswordReadResult(
            RecoveryPasswordReadStatus.Success, ValidPassword, "{" + Protector.ToUpperInvariant() + "}"));

        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key, Volume, [Protector], Protector, alreadyEscrowed: false);

        result.Outcome.ShouldBe(AutomaticEscrowOutcome.Sealed);
    }

    /// <summary>
    /// A result object is exactly what gets captured by a structured-logging scope,
    /// so it must not render the password even when something asks it to.
    /// </summary>
    [Fact]
    public void A_read_result_never_renders_the_password()
    {
        var rendered = new RecoveryPasswordReadResult(
            RecoveryPasswordReadStatus.Success, ValidPassword, Protector).ToString();

        rendered.ShouldNotContain(ValidPassword);
        rendered.ShouldContain("redacted");
    }

    /// <summary>
    /// Proves the envelope really is decryptable by the private-key holder, so the
    /// leakage assertions above are not passing because the data is simply broken.
    /// </summary>
    [Fact]
    public async Task Only_the_private_key_holder_can_recover_the_password()
    {
        var reader = SpyReader.Returning(ValidPassword);
        using var key = NewKey();

        var result = await Gate(reader).TrySealAsync(
            Credential(RecoveryPasswordSealer.Fingerprint(key)),
            key, Volume, [Protector], Protector, alreadyEscrowed: false);

        var envelope = result.Envelope!;

        var dataKey = key.Decrypt(
            Convert.FromBase64String(envelope.WrappedKey), RSAEncryptionPadding.OaepSHA256);

        var plaintext = new byte[Convert.FromBase64String(envelope.Ciphertext).Length];

        using (var aes = new AesGcm(dataKey, 16))
        {
            aes.Decrypt(
                Convert.FromBase64String(envelope.Nonce),
                Convert.FromBase64String(envelope.Ciphertext),
                Convert.FromBase64String(envelope.Tag),
                plaintext);
        }

        Encoding.UTF8.GetString(plaintext).ShouldBe(ValidPassword);
    }
}
