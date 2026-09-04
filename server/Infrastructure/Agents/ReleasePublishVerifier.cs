using System.Security.Cryptography;
using EndpointPlatform.Domain.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Agents;

/// <summary>
/// How much an agent release must prove before the platform will push it onto
/// machines.
/// </summary>
/// <remarks>
/// <para>
/// A mode, not a switch. "RequireCertificate = false" would be a global that
/// quietly turns a security control off; a trust mode is a statement about the
/// deployment the platform is running in, and every check that depends on it is
/// spelled out against the mode by name. The default is <see cref="Internal"/>
/// because that is what Techsara currently is: one company, one private network,
/// controlled PCs, no public distribution.
/// </para>
/// <para>
/// What does <em>not</em> vary by mode: the SHA-256 is always the server's, always
/// re-checked against the stored bytes at publish, and always re-checked by the
/// agent over the downloaded bytes; the artifact must always be a Windows
/// Installer package whose own ProductVersion is the version the release
/// declares; publishing always needs an authorized administrator, a Draft
/// release, and an audit entry; transport is always HTTPS. Only the Authenticode
/// requirement is a matter of mode.
/// </para>
/// </remarks>
public enum AgentReleaseTrustMode
{
    /// <summary>
    /// Company deployment. Integrity comes from the server-computed SHA-256,
    /// verified at publish and again by the agent at install, over HTTPS, under
    /// authorization and audit. No CA-issued Authenticode signature is required
    /// and none is checked. An MSI published this way is <em>not</em> thereby
    /// trusted by Windows itself; it is trusted by this platform, for these
    /// machines.
    /// </summary>
    Internal = 0,

    /// <summary>
    /// Public distribution. Everything Internal requires, plus a valid Authenticode
    /// signature from a certificate that chains to a trusted root, carries the
    /// Code Signing EKU, and belongs to the configured publisher.
    /// </summary>
    Public = 1,
}

/// <summary>Why a release may not be published, or null when it may.</summary>
public enum ReleaseVerificationFailure
{
    /// <summary>The bytes are not in the content store.</summary>
    ArtifactMissing,

    /// <summary>The bytes are not an OLE2 compound file, so not a Windows Installer package.</summary>
    NotAnMsi,

    /// <summary>The stored bytes no longer hash to the release's recorded SHA-256.</summary>
    HashMismatch,

    /// <summary>
    /// The package's ProductVersion could not be read: no Windows Installer
    /// database inside the compound file, no Property table, no ProductVersion
    /// row, or streams that do not decode.
    /// </summary>
    ProductVersionUnavailable,

    /// <summary>The package's ProductVersion is not the version the release declares.</summary>
    ProductVersionMismatch,

    /// <summary>The same bytes are already the artifact of a release with a different version.</summary>
    DuplicateArtifact,

    /// <summary>Public mode only: the Authenticode requirement was not met.</summary>
    SignatureRequired,
}

/// <summary>The outcome of verifying one release for publication.</summary>
/// <param name="Authenticode">
/// In Public mode, the Authenticode result that produced <see cref="ReleaseVerificationFailure.SignatureRequired"/>
/// or the verified signer. Always null in Internal mode: that path is never taken.
/// </param>
/// <param name="Detail">
/// The specifics an administrator needs to act -- the two versions that disagree,
/// the release that already owns the bytes. Never anything about the bytes
/// themselves.
/// </param>
public sealed record ReleaseVerification(
    ReleaseVerificationFailure? Failure,
    AgentReleaseTrustMode Mode,
    AuthenticodeVerification? Authenticode,
    string? Detail)
{
    public bool IsTrusted => Failure is null;

    /// <summary>The verified signer, when the mode verifies one.</summary>
    public string? SignerSubject => Authenticode?.IsTrusted == true ? Authenticode.SignerSubject : null;

    /// <summary>The failure's name, for an audit trail that is searched by category.</summary>
    public string? Category => Failure?.ToString();

    public static ReleaseVerification Trusted(AgentReleaseTrustMode mode, AuthenticodeVerification? authenticode = null) =>
        new(null, mode, authenticode, null);

    public static ReleaseVerification Failed(
        ReleaseVerificationFailure failure, AgentReleaseTrustMode mode, string? detail = null, AuthenticodeVerification? authenticode = null) =>
        new(failure, mode, authenticode, detail);

    /// <summary>A message safe to return to an administrator. Names the check, never the bytes.</summary>
    public string Describe() => Failure switch
    {
        null => Mode == AgentReleaseTrustMode.Internal
            ? "The artifact is a Windows Installer package whose stored bytes match its recorded SHA-256 and whose ProductVersion matches the release."
            : "The artifact's bytes match its recorded SHA-256, its ProductVersion matches the release, and it is signed by the expected publisher.",
        ReleaseVerificationFailure.ArtifactMissing => "The release's artifact is missing from storage.",
        ReleaseVerificationFailure.NotAnMsi => "The artifact is not a Windows Installer package.",
        ReleaseVerificationFailure.HashMismatch => "The stored artifact does not match the release's recorded SHA-256.",
        ReleaseVerificationFailure.ProductVersionUnavailable =>
            "The artifact's ProductVersion could not be read. " + (Detail ?? "It does not carry a readable Windows Installer Property table."),
        ReleaseVerificationFailure.ProductVersionMismatch =>
            "The declared release version does not match the MSI. " + (Detail ?? "Declared release and MSI ProductVersion differ."),
        ReleaseVerificationFailure.DuplicateArtifact =>
            "The artifact already belongs to another release version. " + (Detail ?? "One build is one release; upload a build made for this version."),
        ReleaseVerificationFailure.SignatureRequired => Authenticode?.Describe()
            ?? "Public releases must be Authenticode-signed by the configured publisher.",
        _ => "The artifact could not be verified.",
    };

    /// <summary>The wording for a version mismatch, kept in one place so console and audit agree.</summary>
    public static string VersionMismatchDetail(string declaredVersion, string productVersion) =>
        $"Declared release: {declaredVersion} · MSI ProductVersion: {productVersion}";
}

/// <summary>Decides whether stored release bytes may be published.</summary>
public interface IReleasePublishVerifier
{
    /// <summary>The mode this platform is configured for.</summary>
    AgentReleaseTrustMode Mode { get; }

    /// <summary>
    /// Verifies the bytes on disk against the release's recorded hash and
    /// declared version, per the mode.
    /// </summary>
    ReleaseVerification Verify(ReadOnlyMemory<byte>? storedBytes, string recordedSha256, string declaredVersion);
}

/// <summary>
/// The publish gate, with the trust mode deciding how far it goes.
/// </summary>
/// <remarks>
/// <para>
/// The mode-independent checks run first and are the ones that hold the whole
/// model up: the artifact exists, it is a Windows Installer package, its bytes
/// still hash to what the release row says, and the ProductVersion inside it is
/// the version the release declares. A build that fails any of these is refused
/// in every mode, whatever it is signed with.
/// </para>
/// <para>
/// The ProductVersion check is what makes the row and the bytes one thing. Before
/// it, a release row said "1.5.1" and its artifact said "1.5.0", and nothing
/// noticed until an agent did. The version is read out of the package by
/// <see cref="MsiDatabase"/> rather than trusted from the upload form, and a
/// package whose version cannot be read is refused rather than assumed: the
/// declared version is never rewritten to match, because the point is that the
/// two must agree, not that one must win.
/// </para>
/// <para>
/// Only then, and only in <see cref="AgentReleaseTrustMode.Public"/>, does the
/// Authenticode verifier run. In <see cref="AgentReleaseTrustMode.Internal"/> it is
/// not consulted at all -- no signature stream is read, no certificate is
/// examined, no publisher is compared. That is the contract: Internal is not
/// "Public with the check turned off", it is a mode in which the check does not
/// exist. The <see cref="AuthenticodeVerifier"/> is retained, tested, and reachable
/// for the day this platform distributes publicly, and reaching it is a
/// configuration change, not a code change.
/// </para>
/// </remarks>
public sealed class ReleasePublishVerifier(
    IOptions<AgentReleaseOptions> options,
    IAuthenticodeVerifier authenticode,
    ILogger<ReleasePublishVerifier> logger) : IReleasePublishVerifier
{
    private readonly AgentReleaseOptions _options = options.Value;
    private readonly IAuthenticodeVerifier _authenticode = authenticode;
    private readonly ILogger<ReleasePublishVerifier> _logger = logger;

    public AgentReleaseTrustMode Mode => _options.TrustMode;

    public ReleaseVerification Verify(ReadOnlyMemory<byte>? storedBytes, string recordedSha256, string declaredVersion)
    {
        var mode = _options.TrustMode;

        if (storedBytes is not { } bytes)
        {
            return ReleaseVerification.Failed(ReleaseVerificationFailure.ArtifactMissing, mode);
        }

        // The shape check is structural, not cryptographic: an MSI is an OLE2
        // compound file. It does not read the signature stream and applies in both modes.
        if (!CompoundFile.IsCompoundFile(bytes.Span))
        {
            return ReleaseVerification.Failed(ReleaseVerificationFailure.NotAnMsi, mode);
        }

        // The integrity check that every mode rests on. Recomputed from the bytes
        // on disk right now, not trusted from upload time.
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        if (!string.Equals(actual, recordedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Agent release artifact hashes to {Actual} but the release records {Recorded}; refusing to publish.",
                actual, recordedSha256);
            return ReleaseVerification.Failed(ReleaseVerificationFailure.HashMismatch, mode);
        }

        // Bytes proven intact; now they must be the build the row says they are.
        var product = MsiDatabase.TryReadProductVersion(bytes);
        if (!product.IsFound)
        {
            return ReleaseVerification.Failed(
                ReleaseVerificationFailure.ProductVersionUnavailable, mode, DescribeUnavailable(product.Outcome));
        }

        if (!AgentVersionNumber.TryParse(product.Value, out var productVersion)
            || !AgentVersionNumber.TryParse(declaredVersion, out var declared)
            || productVersion != declared)
        {
            _logger.LogWarning(
                "Agent release declares {Declared} but its MSI ProductVersion is {Product}; refusing to publish.",
                declaredVersion, product.Value);
            return ReleaseVerification.Failed(
                ReleaseVerificationFailure.ProductVersionMismatch, mode,
                ReleaseVerification.VersionMismatchDetail(declaredVersion, product.Value!));
        }

        if (mode == AgentReleaseTrustMode.Internal)
        {
            // Deliberately the end of the road. Nothing below this line is reached.
            return ReleaseVerification.Trusted(mode);
        }

        var authenticode = _authenticode.Verify(bytes);
        return authenticode.IsTrusted
            ? ReleaseVerification.Trusted(mode, authenticode)
            : ReleaseVerification.Failed(ReleaseVerificationFailure.SignatureRequired, mode, authenticode.Describe(), authenticode);
    }

    private static string DescribeUnavailable(MsiProductVersionOutcome outcome) => outcome switch
    {
        MsiProductVersionOutcome.NoStringPool => "The file has no Windows Installer database inside it.",
        MsiProductVersionOutcome.NoPropertyTable => "The package has no Property table.",
        MsiProductVersionOutcome.NotDeclared => "The package's Property table declares no ProductVersion.",
        _ => "The package's database could not be decoded.",
    };
}
