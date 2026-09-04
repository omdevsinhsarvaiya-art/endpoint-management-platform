using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Agents;

/// <summary>
/// The publish gate under each trust mode.
/// </summary>
/// <remarks>
/// <para>
/// Internal is the production model, so it gets the fuller treatment: an unsigned
/// MSI publishes; a hash mismatch, a non-MSI, or a missing artifact never does;
/// and -- the assertion the whole design rests on -- the Authenticode verifier is
/// <em>never called</em>. Not "called and ignored": never called. A spy counts.
/// </para>
/// <para>
/// Public is retained for a future distribution model and is asserted to still
/// work: the same mode-independent checks, then the signature. The Authenticode
/// verifier itself keeps its own tests.
/// </para>
/// </remarks>
public sealed class ReleasePublishVerifierTests
{
    /// <summary>Counts calls and returns a canned answer. Internal mode must leave it at zero.</summary>
    private sealed class SpyAuthenticode(AuthenticodeVerification answer) : IAuthenticodeVerifier
    {
        public int Calls { get; private set; }

        public AuthenticodeVerification Verify(ReadOnlyMemory<byte> msi)
        {
            Calls++;
            return answer;
        }
    }

    private static (ReleasePublishVerifier Verifier, SpyAuthenticode Spy) Build(
        AgentReleaseTrustMode mode, AuthenticodeVerification? authenticodeAnswer = null, string? signer = "CN=Techsara")
    {
        var spy = new SpyAuthenticode(authenticodeAnswer ?? AuthenticodeVerification.Trusted("CN=Techsara Test Signing"));
        var options = Options.Create(new AgentReleaseOptions { TrustMode = mode, ExpectedSignerSubject = signer });
        return (new ReleasePublishVerifier(options, spy, NullLogger<ReleasePublishVerifier>.Instance), spy);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- Internal: the production model ------------------------------------

    [Fact]
    public void Internal_is_the_default_mode()
    {
        new AgentReleaseOptions().TrustMode.ShouldBe(AgentReleaseTrustMode.Internal);
    }

    /// <summary>Acceptance criterion 1: an unsigned MSI publishes under Internal.</summary>
    [Fact]
    public void Internal_trusts_an_unsigned_msi_whose_bytes_match_its_hash()
    {
        var (verifier, spy) = Build(AgentReleaseTrustMode.Internal);
        var msi = TestArtifacts.UnsignedMsi();

        var result = verifier.Verify(msi, Sha(msi), TestArtifacts.DefaultProductVersion);

        result.IsTrusted.ShouldBeTrue(result.Describe());
        result.Mode.ShouldBe(AgentReleaseTrustMode.Internal);
        result.SignerSubject.ShouldBeNull("Internal records no signer, because it reads no signature");
        spy.Calls.ShouldBe(0, "Internal mode must never consult the Authenticode verifier");
    }

    /// <summary>
    /// The contract in one test. Even a build that <em>is</em> signed does not
    /// have its signature read under Internal: the verifier is not on the path.
    /// </summary>
    [Fact]
    public void Internal_never_calls_the_authenticode_verifier_even_for_a_signed_msi()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var (verifier, spy) = Build(AgentReleaseTrustMode.Internal);
        var signed = TestArtifacts.SignedMsi(authority);

        var result = verifier.Verify(signed, Sha(signed), TestArtifacts.DefaultProductVersion);

        result.IsTrusted.ShouldBeTrue();
        result.Authenticode.ShouldBeNull();
        result.SignerSubject.ShouldBeNull();
        spy.Calls.ShouldBe(0);
    }

    /// <summary>Internal needs no publisher. An unset one changes nothing.</summary>
    [Fact]
    public void Internal_does_not_require_an_expected_signer()
    {
        foreach (var unset in new[] { null, "", "   " })
        {
            var (verifier, spy) = Build(AgentReleaseTrustMode.Internal, signer: unset);
            var msi = TestArtifacts.UnsignedMsi();

            verifier.Verify(msi, Sha(msi), TestArtifacts.DefaultProductVersion).IsTrusted.ShouldBeTrue();
            spy.Calls.ShouldBe(0);
        }
    }

    /// <summary>Acceptance criterion 2: integrity still fails closed under Internal.</summary>
    [Fact]
    public void Internal_refuses_bytes_that_do_not_match_the_recorded_hash()
    {
        var (verifier, spy) = Build(AgentReleaseTrustMode.Internal);
        var msi = TestArtifacts.UnsignedMsi();
        var tampered = (byte[])msi.Clone();
        tampered[^1] ^= 0x01;

        var result = verifier.Verify(tampered, Sha(msi), TestArtifacts.DefaultProductVersion);

        result.IsTrusted.ShouldBeFalse();
        result.Failure.ShouldBe(ReleaseVerificationFailure.HashMismatch);
        spy.Calls.ShouldBe(0);
    }

    [Fact]
    public void Internal_refuses_bytes_that_are_not_a_windows_installer_package()
    {
        var (verifier, _) = Build(AgentReleaseTrustMode.Internal);
        var bytes = Encoding.ASCII.GetBytes("MZ an exe, not an msi");

        verifier.Verify(bytes, Sha(bytes), TestArtifacts.DefaultProductVersion).Failure.ShouldBe(ReleaseVerificationFailure.NotAnMsi);
    }

    [Fact]
    public void Internal_refuses_a_missing_artifact()
    {
        var (verifier, _) = Build(AgentReleaseTrustMode.Internal);

        verifier.Verify(null, new string('a', 64), TestArtifacts.DefaultProductVersion).Failure.ShouldBe(ReleaseVerificationFailure.ArtifactMissing);
    }

    /// <summary>The recorded hash is compared case-insensitively; storage is lower-case, callers may not be.</summary>
    [Fact]
    public void The_hash_comparison_is_case_insensitive()
    {
        var (verifier, _) = Build(AgentReleaseTrustMode.Internal);
        var msi = TestArtifacts.UnsignedMsi();

        verifier.Verify(msi, Sha(msi).ToUpperInvariant(), TestArtifacts.DefaultProductVersion).IsTrusted.ShouldBeTrue();
    }

    // ---- Public: retained for a future distribution model -------------------

    [Fact]
    public void Public_consults_the_authenticode_verifier_after_the_integrity_checks()
    {
        var (verifier, spy) = Build(AgentReleaseTrustMode.Public);
        var msi = TestArtifacts.UnsignedMsi(); // shape and hash fine; the spy decides trust

        var result = verifier.Verify(msi, Sha(msi), TestArtifacts.DefaultProductVersion);

        result.IsTrusted.ShouldBeTrue();
        result.SignerSubject.ShouldBe("CN=Techsara Test Signing");
        spy.Calls.ShouldBe(1);
    }

    [Fact]
    public void Public_refuses_when_the_authenticode_verifier_refuses()
    {
        var refusal = AuthenticodeVerification.Failed(AuthenticodeFailure.Unsigned);
        var (verifier, spy) = Build(AgentReleaseTrustMode.Public, refusal);
        var msi = TestArtifacts.UnsignedMsi();

        var result = verifier.Verify(msi, Sha(msi), TestArtifacts.DefaultProductVersion);

        result.Failure.ShouldBe(ReleaseVerificationFailure.SignatureRequired);
        result.Authenticode.ShouldBe(refusal);
        result.Describe().ShouldContain("not Authenticode-signed");
        spy.Calls.ShouldBe(1);
    }

    /// <summary>Integrity before signature, in Public too: a tampered signed build never reaches the verifier.</summary>
    [Fact]
    public void Public_checks_the_hash_before_ever_looking_at_the_signature()
    {
        var (verifier, spy) = Build(AgentReleaseTrustMode.Public);
        var msi = TestArtifacts.UnsignedMsi();

        verifier.Verify(msi, new string('0', 64), TestArtifacts.DefaultProductVersion).Failure.ShouldBe(ReleaseVerificationFailure.HashMismatch);
        spy.Calls.ShouldBe(0);
    }

    /// <summary>Public without a configured publisher is an incoherent configuration, refused at startup.</summary>
    [Fact]
    public void Public_without_an_expected_signer_is_invalid_configuration()
    {
        new AgentReleaseOptions { TrustMode = AgentReleaseTrustMode.Public, ExpectedSignerSubject = null }.IsValid.ShouldBeFalse();
        new AgentReleaseOptions { TrustMode = AgentReleaseTrustMode.Public, ExpectedSignerSubject = "CN=X" }.IsValid.ShouldBeTrue();
        new AgentReleaseOptions { TrustMode = AgentReleaseTrustMode.Internal, ExpectedSignerSubject = null }.IsValid.ShouldBeTrue();
    }

    // ---- messages ----------------------------------------------------------

    [Fact]
    public void Every_failure_describes_itself_without_leaking_bytes()
    {
        foreach (var mode in new[] { AgentReleaseTrustMode.Internal, AgentReleaseTrustMode.Public })
        {
            foreach (ReleaseVerificationFailure failure in Enum.GetValues<ReleaseVerificationFailure>())
            {
                var text = ReleaseVerification.Failed(failure, mode).Describe();

                text.Length.ShouldBeGreaterThan(20);
                text.ShouldNotContain("0x");
                text.ShouldNotContain("Exception");
            }
        }
    }

    /// <summary>Internal's success message must not imply a signature was checked.</summary>
    [Fact]
    public void Internal_success_does_not_claim_a_signature()
    {
        var text = ReleaseVerification.Trusted(AgentReleaseTrustMode.Internal).Describe();

        text.ShouldContain("SHA-256");
        text.ShouldNotContain("signed");
    }
}
