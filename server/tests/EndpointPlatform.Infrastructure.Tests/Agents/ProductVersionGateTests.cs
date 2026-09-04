using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Agents;

/// <summary>
/// The publish gate's newest mode-independent check: the package's own
/// ProductVersion must be the version the release declares.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a release row once said 1.5.1 while its artifact said
/// 1.5.0, and nothing compared them. The declared version came from a form; the
/// bytes were hashed but never read. So: read them. Declared and actual must
/// agree in both directions, an unreadable version is a refusal rather than an
/// assumption, and the declared version is never rewritten to match -- the
/// point is that the two agree, not that one wins.
/// </para>
/// <para>
/// The check sits with the other mode-independent ones, after the hash and
/// before the trust mode has any say. Internal's contract is preserved: the
/// Authenticode verifier is still never called, whichever way this check goes.
/// </para>
/// </remarks>
public sealed class ProductVersionGateTests
{
    private sealed class SpyAuthenticode : IAuthenticodeVerifier
    {
        public int Calls { get; private set; }

        public AuthenticodeVerification Verify(ReadOnlyMemory<byte> msi)
        {
            Calls++;
            return AuthenticodeVerification.Trusted("CN=Techsara Test Signing");
        }
    }

    private static (ReleasePublishVerifier Verifier, SpyAuthenticode Spy) Build(AgentReleaseTrustMode mode = AgentReleaseTrustMode.Internal)
    {
        var spy = new SpyAuthenticode();
        var options = Options.Create(new AgentReleaseOptions { TrustMode = mode, ExpectedSignerSubject = "CN=Techsara" });
        return (new ReleasePublishVerifier(options, spy, NullLogger<ReleasePublishVerifier>.Instance), spy);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- A: agreement ----------------------------------------------------------

    [Fact]
    public void A_declared_version_equal_to_the_msi_product_version_is_accepted()
    {
        var (verifier, spy) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        var result = verifier.Verify(msi, Sha(msi), "1.7.0");

        result.IsTrusted.ShouldBeTrue(result.Describe());
        spy.Calls.ShouldBe(0, "Internal never consults the Authenticode verifier");
    }

    /// <summary>Both sides are normalised: leading zeros and whitespace are not a disagreement.</summary>
    [Theory]
    [InlineData("1.7.0", " 1.7.0 ")]
    [InlineData("01.7.0", "1.7.0")]
    [InlineData("1.7.0", "1.07.00")]
    public void Versions_are_compared_numerically_not_textually(string declared, string inMsi)
    {
        var (verifier, _) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: inMsi);

        verifier.Verify(msi, Sha(msi), declared).IsTrusted.ShouldBeTrue();
    }

    // ---- B and C: disagreement, either way round ------------------------------

    [Fact]
    public void A_declared_version_ahead_of_the_msi_is_refused()
    {
        var (verifier, spy) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        var result = verifier.Verify(msi, Sha(msi), "1.7.1");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionMismatch);
        result.Describe().ShouldContain("Declared release: 1.7.1");
        result.Describe().ShouldContain("MSI ProductVersion: 1.7.0");
        spy.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_declared_version_behind_the_msi_is_refused()
    {
        var (verifier, _) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.1");

        var result = verifier.Verify(msi, Sha(msi), "1.7.0");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionMismatch);
        result.Describe().ShouldContain("Declared release: 1.7.0");
        result.Describe().ShouldContain("MSI ProductVersion: 1.7.1");
    }

    /// <summary>
    /// The 1.5.1 case exactly: the 1.5.0 package declared as 1.5.1. The refusal
    /// names both, which is what an operator needs to see to know which is wrong.
    /// </summary>
    [Fact]
    public void The_1_5_1_case_is_refused_and_names_both_versions()
    {
        var (verifier, _) = Build();
        var package150 = TestArtifacts.UnsignedMsi(productVersion: "1.5.0");

        var result = verifier.Verify(package150, Sha(package150), "1.5.1");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionMismatch);
        result.Detail.ShouldBe("Declared release: 1.5.1 · MSI ProductVersion: 1.5.0");
    }

    /// <summary>A four-part or otherwise non-platform version in the MSI cannot equal a declared one.</summary>
    [Theory]
    [InlineData("1.7.0.0")]
    [InlineData("1.7")]
    [InlineData("v1.7.0")]
    [InlineData("1.7.0-beta")]
    public void A_product_version_outside_the_platform_scheme_is_a_mismatch_that_shows_the_value(string inMsi)
    {
        var (verifier, _) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: inMsi);

        var result = verifier.Verify(msi, Sha(msi), "1.7.0");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionMismatch);
        result.Describe().ShouldContain(inMsi);
    }

    // ---- D and E: nothing to compare -------------------------------------------

    [Fact]
    public void A_compound_file_with_no_installer_database_is_refused()
    {
        var (verifier, _) = Build();
        var shell = TestArtifacts.MsiWithoutDatabase("no-db");

        var result = verifier.Verify(shell, Sha(shell), "1.0.0");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionUnavailable);
        result.Describe().ShouldContain("no Windows Installer database");
    }

    [Fact]
    public void A_package_that_declares_no_product_version_is_refused()
    {
        var (verifier, _) = Build();
        var msi = TestArtifacts.MsiWithProperties([("ProductName", "Nameless build")]);

        var result = verifier.Verify(msi, Sha(msi), "1.0.0");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionUnavailable);
        result.Describe().ShouldContain("declares no ProductVersion");
    }

    [Fact]
    public void A_package_whose_database_does_not_decode_is_refused()
    {
        var (verifier, _) = Build();
        var streams = TestArtifacts.MsiDatabaseStreams(TestArtifacts.AgentProperties("1.0.0")).ToList();
        var pool = streams.FindIndex(s => s.Name == MsiDatabase.EncodeStreamName("_StringPool", database: true));
        streams[pool] = (streams[pool].Name, streams[pool].Payload[..^1]);
        var msi = TestArtifacts.CompoundFile(streams);

        var result = verifier.Verify(msi, Sha(msi), "1.0.0");

        result.Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionUnavailable);
        result.Describe().ShouldContain("could not be decoded");
    }

    // ---- order ------------------------------------------------------------------

    /// <summary>Integrity first: a version is not read out of bytes that failed their hash.</summary>
    [Fact]
    public void The_hash_is_checked_before_the_version_is_read()
    {
        var (verifier, _) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        verifier.Verify(msi, new string('0', 64), "9.9.9").Failure
            .ShouldBe(ReleaseVerificationFailure.HashMismatch);
    }

    [Fact]
    public void Shape_is_checked_before_the_version_is_read()
    {
        var (verifier, _) = Build();
        var exe = Encoding.ASCII.GetBytes("MZ not a package");

        verifier.Verify(exe, Sha(exe), "1.0.0").Failure.ShouldBe(ReleaseVerificationFailure.NotAnMsi);
    }

    /// <summary>In Public, a version mismatch is refused before the signature is ever looked at.</summary>
    [Fact]
    public void Public_refuses_a_version_mismatch_without_consulting_the_signature()
    {
        var (verifier, spy) = Build(AgentReleaseTrustMode.Public);
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        verifier.Verify(msi, Sha(msi), "1.7.1").Failure.ShouldBe(ReleaseVerificationFailure.ProductVersionMismatch);
        spy.Calls.ShouldBe(0);
    }

    [Fact]
    public void Public_reaches_the_signature_only_once_the_version_agrees()
    {
        var (verifier, spy) = Build(AgentReleaseTrustMode.Public);
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        verifier.Verify(msi, Sha(msi), "1.7.0").IsTrusted.ShouldBeTrue();
        spy.Calls.ShouldBe(1);
    }

    // ---- messages ---------------------------------------------------------------

    [Fact]
    public void Refusals_name_the_versions_and_nothing_about_the_bytes()
    {
        var (verifier, _) = Build();
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        var text = verifier.Verify(msi, Sha(msi), "1.7.1").Describe();

        text.ShouldNotContain("0x");
        text.ShouldNotContain("Exception");
        text.ShouldNotContain("stream");
        text.ShouldContain("1.7.1");
        text.ShouldContain("1.7.0");
    }

    [Fact]
    public void The_duplicate_artifact_failure_describes_itself_with_and_without_detail()
    {
        ReleaseVerification.Failed(ReleaseVerificationFailure.DuplicateArtifact, AgentReleaseTrustMode.Internal)
            .Describe().ShouldContain("already belongs to another release");

        ReleaseVerification.Failed(
                ReleaseVerificationFailure.DuplicateArtifact, AgentReleaseTrustMode.Internal, "Release 1.5.0 (Published) already uses this artifact.")
            .Describe().ShouldContain("1.5.0 (Published)");
    }

    [Fact]
    public void Every_failure_has_a_category_for_the_audit_trail()
    {
        foreach (ReleaseVerificationFailure failure in Enum.GetValues<ReleaseVerificationFailure>())
        {
            ReleaseVerification.Failed(failure, AgentReleaseTrustMode.Internal).Category.ShouldBe(failure.ToString());
        }

        ReleaseVerification.Trusted(AgentReleaseTrustMode.Internal).Category.ShouldBeNull();
    }
}
