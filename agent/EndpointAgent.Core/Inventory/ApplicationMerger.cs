namespace EndpointAgent.Core.Inventory;

/// <summary>
/// Turns everything the discovery sources reported into a list of applications.
/// </summary>
/// <remarks>
/// <para>
/// Each piece of evidence is identified (<see cref="ApplicationIdentity"/>),
/// classified (<see cref="ApplicationCategory"/>) and given a confidence
/// (<see cref="DiscoveryConfidence"/>). What survives is a
/// <see cref="DiscoveredApplication"/> carrying the evidence that produced it, so
/// "why does Techsara believe this application exists?" has an answer that is
/// data rather than inference.
/// </para>
/// <para>
/// <b>What this deliberately does not do yet.</b> It does not fold several
/// sources' evidence into one application. Folding means keying applications on
/// <see cref="ApplicationIdentity.StableKey"/>, and that key excludes the version
/// on purpose -- an update must replace a row, not add one. The pipeline's final
/// step, <see cref="SoftwareInventoryNormalizer"/>, keys on
/// (name, version, publisher, scope, user) and therefore keeps two installed
/// versions of one product visible. Both rules are right for their own job, and
/// reconciling them changes what a machine reports.
/// </para>
/// <para>
/// So the reconciliation happens in the phase that needs it -- when a second
/// source starts describing an application the first source already found -- with
/// its own tests and its own measured before/after on a real machine. Until then
/// this is a one-to-one projection, and the inventory a machine reports is
/// byte-for-byte what it reported before the abstraction existed. That property is
/// asserted, not assumed.
/// </para>
/// </remarks>
public static class ApplicationMerger
{
    /// <summary>
    /// Names whose presence marks software that exists for other software rather
    /// than for a person.
    /// </summary>
    /// <remarks>
    /// Matched against the display name only, and used to <em>label</em> -- never
    /// to drop. An administrator can hide a category; the platform still collected
    /// it, because "what runtimes are on this fleet" is a real security question.
    /// </remarks>
    private static readonly string[] RuntimeMarkers =
    [
        "redistributable",
        "runtime",
        " sdk",
        "sdk ",
        "software development kit",
        "python launcher",
        ".net host",
        "targeting pack",
    ];

    private static readonly string[] ComponentMarkers =
    [
        "compatibility fix database",
        "servicing stack",
        "language pack",
    ];

    /// <summary>
    /// One application per piece of evidence, identified and classified.
    /// </summary>
    /// <remarks>
    /// Evidence that identifies nothing is dropped rather than becoming a row with
    /// no identity: a discovery subsystem that invents applications is worse than
    /// one that misses them.
    /// </remarks>
    public static IReadOnlyList<DiscoveredApplication> Merge(IEnumerable<SoftwareEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var applications = new List<DiscoveredApplication>();

        foreach (var item in evidence)
        {
            if (item is null)
            {
                continue;
            }

            var identity = ApplicationIdentity.Derive(item);
            if (identity is null)
            {
                continue;
            }

            var name = DisplayName(item);
            if (name is null)
            {
                // Nothing to call it. The uninstall reader already skips entries
                // with no DisplayName; this is the same rule applied to every
                // source that will follow.
                continue;
            }

            applications.Add(new DiscoveredApplication(
                identity,
                name,
                item.Version,
                item.Publisher,
                item.InstallDate,
                item.InstallLocation,
                item.RegistryView,
                item.Scope,
                item.InstalledForUser,
                item.ProductCode,
                ConfidenceOf(item),
                CategoryOf(item, name),
                [item]));
        }

        return applications;
    }

    /// <summary>What to call the application, from what this evidence knows.</summary>
    private static string? DisplayName(SoftwareEvidence evidence)
    {
        if (!string.IsNullOrWhiteSpace(evidence.Name))
        {
            return evidence.Name.Trim();
        }

        // A source that found only an executable still names something: the file.
        // Better than dropping a running application because nothing declared a
        // display name for it.
        if (!string.IsNullOrWhiteSpace(evidence.ExecutablePath))
        {
            try
            {
                var file = Path.GetFileNameWithoutExtension(evidence.ExecutablePath.Trim());
                return string.IsNullOrWhiteSpace(file) ? null : file;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// What this evidence supports claiming.
    /// </summary>
    /// <remarks>
    /// An installation record makes an application installed. A process makes it
    /// present. A shortcut or an execution alias makes it referenced -- something
    /// points at it, which is not the same as it being there, and is exactly what
    /// a shortcut left behind by an uninstall looks like.
    /// </remarks>
    private static DiscoveryConfidence ConfidenceOf(SoftwareEvidence evidence) => evidence switch
    {
        { IsAuthoritative: true } => DiscoveryConfidence.Installed,
        { Source: EvidenceSource.RunningProcess } => DiscoveryConfidence.Observed,
        _ => DiscoveryConfidence.Referenced,
    };

    private static ApplicationCategory CategoryOf(SoftwareEvidence evidence, string name)
    {
        if (evidence.Source == EvidenceSource.RunningProcess)
        {
            return ApplicationCategory.Observed;
        }

        var lowered = name.ToLowerInvariant();

        if (ComponentMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal)))
        {
            return ApplicationCategory.Component;
        }

        return RuntimeMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal))
            ? ApplicationCategory.RuntimeOrSdk
            : ApplicationCategory.Application;
    }
}
