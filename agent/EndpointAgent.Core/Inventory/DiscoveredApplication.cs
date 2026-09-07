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
    /// <summary>
    /// The shape the existing inventory pipeline consumes.
    /// </summary>
    /// <remarks>
    /// Deliberately lossy, and deliberately unchanged: the wire contract this
    /// eventually reaches carries the nine fields it has always carried, and the
    /// richer identity and evidence are appended to it in a later phase rather
    /// than reshaping what already works. Until then this projection is what keeps
    /// the new pipeline's output identical to the old one's.
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
        ProductCode);
}
