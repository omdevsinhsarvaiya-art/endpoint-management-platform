using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Inventory;

/// <summary>
/// One application, as everything the endpoint knows about it adds up.
/// </summary>
/// <remarks>
/// <para>
/// The output of <see cref="ApplicationMerger"/> and the input to
/// <see cref="SoftwareInventoryNormalizer"/>. The descriptive fields are the best
/// answer across the evidence; <see cref="Evidence"/> keeps the individual answers
/// so an administrator can see why the platform believes this application exists,
/// and so a wrong merge is visible rather than silent.
/// </para>
/// <para>
/// Scope and the account are part of the application, not of the evidence that
/// found it: the same product installed for two people is two applications,
/// because uninstalling one leaves the other running. That rule predates this
/// subsystem and is unchanged by it.
/// </para>
/// </remarks>
public sealed record DiscoveredApplication(
    ApplicationIdentity Identity,
    string Name,
    string? Version,
    string? Publisher,
    string? InstallDate,
    string? InstallLocation,
    string? RegistryView,
    SoftwareScope Scope,
    string? InstalledForUser,
    string? ProductCode,
    DiscoveryConfidence Confidence,
    ApplicationCategory Category,
    IReadOnlyList<SoftwareEvidence> Evidence)
{
    /// <summary>The Windows Installer upgrade code, when the product has one.</summary>
    public string? UpgradeCode { get; init; }

    /// <summary>
    /// The application's primary executable, when a supplementary source named
    /// one. Informational: the location Force Stop acts on is still
    /// <see cref="InstallLocation"/>.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The subject the primary executable's embedded signature names, as a claim.</summary>
    public string? SignerSubject { get; init; }

    /// <summary>Whether the primary executable carries an embedded signature; see <see cref="ExecutableSignatureStatus"/>.</summary>
    public string? SignatureStatus { get; init; }

    /// <summary>
    /// The shape the inventory pipeline consumes.
    /// </summary>
    /// <remarks>
    /// The nine fields the report has always carried are projected exactly as
    /// before -- the row a server sees is unchanged in every one of them -- and
    /// the identity, confidence, category, package, signer and evidence fields
    /// discovery adds ride alongside as optional additions. The normalizer clamps
    /// each to its wire limit.
    /// </remarks>
    public DiscoveredSoftware ToDiscoveredSoftware() => new(
        Name,
        Version,
        Publisher,
        InstallDate,
        InstallLocation,
        RegistryView,
        Scope,
        InstalledForUser,
        ProductCode,
        IdentityKind: Identity.Kind.ToString(),
        StableKey: Identity.StableKey,
        VersionKey: Identity.VersionKey,
        Confidence: Confidence.ToString(),
        Category: Category.ToString(),
        PackageFamilyName: Evidence.Select(e => e.PackageFamilyName).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
        PackageFullName: Evidence.Select(e => e.PackageFullName).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
        UpgradeCode: UpgradeCode,
        ExecutablePath: ExecutablePath,
        SignerSubject: SignerSubject,
        SignatureStatus: SignatureStatus,
        Evidence: Evidence.Select(Witness).ToArray());

    /// <summary>
    /// One piece of evidence as the wire carries it: the source, what that
    /// source called the thing, and what it pointed at -- a product code, a
    /// package full name, or an executable. Never a registry path or anything
    /// a source did not itself report.
    /// </summary>
    private static InventorySoftwareEvidence Witness(SoftwareEvidence evidence) => new(
        evidence.Source.ToString(),
        evidence.Name,
        evidence.Source switch
        {
            EvidenceSource.WindowsInstaller => evidence.ProductCode ?? evidence.InstallLocation,
            EvidenceSource.UninstallRegistry => evidence.InstallLocation,
            EvidenceSource.PackageRegistration => evidence.PackageFullName ?? evidence.InstallLocation,
            _ => evidence.ExecutablePath,
        });
}
