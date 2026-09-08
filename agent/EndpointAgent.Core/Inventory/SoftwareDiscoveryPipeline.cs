using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Inventory;

/// <summary>
/// From everything the sources reported to the list the server receives.
/// </summary>
/// <remarks>
/// <para>
/// One function, so the registry collector's own <c>CollectAsync</c> and the
/// composite that runs every source produce the report the same way. A second
/// copy of this sequence would be a second place for the two to disagree.
/// </para>
/// <para>
/// <b>What is reported.</b> An application is a row when it is
/// <see cref="DiscoveryConfidence.Installed"/> or
/// <see cref="DiscoveryConfidence.Observed"/>. A
/// <see cref="DiscoveryConfidence.Referenced"/> application -- a shortcut or an
/// execution alias with nothing behind it -- is a hint, kept as evidence for the
/// application it may belong to, and is not a row of its own. Reporting it would
/// mean a stale shortcut appears in an administrator's inventory as software.
/// </para>
/// </remarks>
public static class SoftwareDiscoveryPipeline
{
    public static IReadOnlyList<InventorySoftware> Run(IEnumerable<SoftwareEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var applications = ApplicationMerger.Merge(evidence);

        return SoftwareInventoryNormalizer.Normalize(
            applications.Where(IsReportable).Select(a => a.ToDiscoveredSoftware()));
    }

    /// <summary>Whether an application is a row in the report, or only a hint.</summary>
    public static bool IsReportable(DiscoveredApplication application) =>
        application.Confidence is DiscoveryConfidence.Installed or DiscoveryConfidence.Observed;
}
