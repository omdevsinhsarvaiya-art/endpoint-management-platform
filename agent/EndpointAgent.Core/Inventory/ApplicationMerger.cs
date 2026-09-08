namespace EndpointAgent.Core.Inventory;

/// <summary>
/// Turns everything the discovery sources reported into a list of applications.
/// </summary>
/// <remarks>
/// <para>
/// Three stages, in order of how much each piece of evidence is allowed to claim.
/// </para>
/// <para>
/// <b>Installations.</b> Authoritative evidence -- an installation record --
/// becomes an application. Several records that would have been one inventory
/// row are one application with several pieces of evidence: the key is exactly
/// the row identity the normalizer has always used (name, version, publisher,
/// scope, account) plus the stable key, so this stage can never fold two rows
/// the inventory kept apart, and the first record's descriptive fields win
/// exactly as they did before. Two installed versions of one product therefore
/// stay two applications. Recognising an update as a replacement is a later
/// concern, and keying on the version here is what keeps that decision out of
/// this one.
/// </para>
/// <para>
/// <b>Attachment.</b> Supplementary evidence -- an alias, a shortcut, a file's own
/// metadata, a process -- names an executable. It attaches to the one
/// installation whose directory contains that executable; when several do, to
/// the one named like it, and otherwise to none. An installation with no known
/// directory may adopt one from a shortcut or a file named exactly like it,
/// published by the same publisher when both say, recorded for the same account,
/// and unambiguous. An adopted directory must be one Force Stop could act on,
/// and a second, different directory for the same application withdraws the
/// adoption rather than choosing between them.
/// </para>
/// <para>
/// <b>References.</b> What attaches to nothing is grouped by the executable it
/// names and becomes an application of its own: Observed when a process is among
/// its evidence, Referenced otherwise. Neither is an installation, and only the
/// first is a row.
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
    /// File names that mark an executable as an installer, updater or
    /// bootstrapper rather than an application: present, but transient.
    /// </summary>
    /// <remarks>
    /// Matched against the executable's file name only, never a display name,
    /// and applied only to executables no installation record covers: an
    /// application's own updater inside its install directory is that
    /// application's evidence, not a transient of its own.
    /// </remarks>
    private static readonly string[] TransientMarkers =
    [
        "setup",
        "install",
        "unins",
        "update",
        "upgrade",
        "bootstrap",
        "msiexec",
    ];

    /// <summary>The same separator the normalizer and the identity use, for the same reason.</summary>
    private const char KeySeparator = (char)0x1F;

    /// <summary>
    /// Everything the sources found, as applications.
    /// </summary>
    /// <remarks>
    /// Evidence that identifies nothing is dropped rather than becoming a row with
    /// no identity: a discovery subsystem that invents applications is worse than
    /// one that misses them.
    /// </remarks>
    public static IReadOnlyList<DiscoveredApplication> Merge(IEnumerable<SoftwareEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var items = evidence.Where(e => e is not null).ToList();

        var installations = MergeInstallations(items.Where(e => e.IsAuthoritative));
        var unattached = Attach(installations, items.Where(e => !e.IsAuthoritative));
        var references = MergeReferences(unattached);

        return [.. installations.Select(i => i.Build()), .. references];
    }

    // ---- stage 1: installations ------------------------------------------------------

    private static List<Installation> MergeInstallations(IEnumerable<SoftwareEvidence> authoritative)
    {
        var byKey = new Dictionary<string, Installation>(StringComparer.OrdinalIgnoreCase);
        var installations = new List<Installation>();

        foreach (var item in authoritative)
        {
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
                // source.
                continue;
            }

            var key = string.Join(KeySeparator, RowKey(name, item), identity.StableKey);
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Absorb(item);
                continue;
            }

            var installation = new Installation(identity, name, item);
            byKey.Add(key, installation);
            installations.Add(installation);
        }

        return installations;
    }

    /// <summary>
    /// The identity the normalizer keys rows on, computed the same way, so that
    /// nothing folds here that would not have folded there.
    /// </summary>
    private static string RowKey(string name, SoftwareEvidence item) =>
        string.Join(
            KeySeparator,
            name.Trim(),
            Trimmed(item.Version),
            Trimmed(item.Publisher),
            item.Scope == SoftwareScope.User ? "User" : "Machine",
            item.Scope == SoftwareScope.User ? Trimmed(item.InstalledForUser) : string.Empty);

    // ---- stage 2: attachment ----------------------------------------------------------

    /// <summary>Attaches what can be attached; returns what cannot.</summary>
    private static List<SoftwareEvidence> Attach(List<Installation> installations, IEnumerable<SoftwareEvidence> supplementary)
    {
        var unattached = new List<SoftwareEvidence>();

        foreach (var item in supplementary)
        {
            var path = ExecutablePath.Normalize(item.ExecutablePath);
            if (path is null)
            {
                unattached.Add(item);
                continue;
            }

            var containing = installations.Where(i => ExecutablePath.IsUnder(path, i.InstallLocation)).ToList();
            if (containing.Count == 1)
            {
                containing[0].Attach(item, path);
                continue;
            }

            if (containing.Count > 1)
            {
                // Several installations share the directory (an office suite does
                // this). The one named like the evidence takes it; if none or
                // several are, nothing does.
                var named = containing.Where(i => i.IsNamed(item.Name)).ToList();
                if (named.Count == 1)
                {
                    named[0].Attach(item, path);
                    continue;
                }

                unattached.Add(item);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                var adopters = installations
                    .Where(i => !i.HasRecordedLocation && i.IsNamed(item.Name) && i.PublisherAgrees(item) && i.IsRecordedFor(item))
                    .ToList();

                if (adopters.Count == 1)
                {
                    adopters[0].Adopt(item, path);
                    continue;
                }
            }

            unattached.Add(item);
        }

        return unattached;
    }

    // ---- stage 3: references ----------------------------------------------------------

    private static List<DiscoveredApplication> MergeReferences(List<SoftwareEvidence> unattached)
    {
        var groups = new Dictionary<string, List<SoftwareEvidence>>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<List<SoftwareEvidence>>();

        foreach (var item in unattached)
        {
            // One application per executable. Evidence naming no executable stands
            // alone; it cannot be shown to be about the same thing as anything else.
            var key = ExecutablePath.Normalize(item.ExecutablePath) ?? $"{KeySeparator}{ordered.Count}";

            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups.Add(key, group);
                ordered.Add(group);
            }

            group.Add(item);
        }

        var applications = new List<DiscoveredApplication>();
        foreach (var group in ordered)
        {
            if (BuildReference(group) is { } application)
            {
                applications.Add(application);
            }
        }

        return applications;
    }

    /// <summary>
    /// One application from everything that named one executable and attached to
    /// no installation.
    /// </summary>
    /// <remarks>
    /// What to call it, in order: the shortcut that points at it, which is what
    /// a person sees; the product name its own version resource declares; whatever
    /// else named it; and finally the file itself. Version, publisher and signer
    /// come from the file's metadata when it was read.
    /// </remarks>
    private static DiscoveredApplication? BuildReference(List<SoftwareEvidence> group)
    {
        var metadata = group.FirstOrDefault(e => e.Source == EvidenceSource.ExecutableMetadata);
        var anchor = group.FirstOrDefault(e => e.Source != EvidenceSource.ExecutableMetadata) ?? group[0];
        var process = group.FirstOrDefault(e => e.Source == EvidenceSource.RunningProcess);
        var path = ExecutablePath.Normalize(anchor.ExecutablePath) ?? ExecutablePath.Normalize(metadata?.ExecutablePath);

        var name = Value(group.FirstOrDefault(e => e.Source == EvidenceSource.StartMenuShortcut)?.Name)
            ?? Value(metadata?.Name)
            ?? group.Select(e => Value(e.Name)).FirstOrDefault(n => n is not null)
            ?? FileName(path);

        if (name is null)
        {
            return null;
        }

        var composite = new SoftwareEvidence(
            process?.Source ?? anchor.Source,
            name,
            Value(metadata?.Version) ?? group.Select(e => Value(e.Version)).FirstOrDefault(v => v is not null),
            Value(metadata?.Publisher) ?? group.Select(e => Value(e.Publisher)).FirstOrDefault(p => p is not null),
            Scope: anchor.Scope,
            InstalledForUser: anchor.InstalledForUser,
            ExecutablePath: path,
            SignerSubject: Value(metadata?.SignerSubject),
            SignatureStatus: Value(metadata?.SignatureStatus));

        var identity = ApplicationIdentity.Derive(composite);
        if (identity is null)
        {
            return null;
        }

        // What the executable is: an installer, updater or a run from a temp
        // folder is transient -- present now, not an application. Otherwise a
        // process makes it observed and anything less makes it referenced.
        var transient = IsTransient(path);
        var confidence = transient
            ? DiscoveryConfidence.Transient
            : process is not null ? DiscoveryConfidence.Observed : DiscoveryConfidence.Referenced;

        // An observed executable's directory is reported as its location only
        // when the directory is its own. A shared folder -- Downloads, a profile
        // root, a temp folder -- or anything under the Windows directory would
        // make Force Stop act on everything there.
        var directory = ExecutablePath.DirectoryOf(path);
        var location = confidence == DiscoveryConfidence.Observed && ExecutablePath.IsAdoptableLocation(directory)
            ? directory
            : null;

        return new DiscoveredApplication(
            identity,
            name,
            composite.Version,
            composite.Publisher,
            InstallDate: null,
            location,
            RegistryView: null,
            composite.Scope,
            composite.InstalledForUser,
            ProductCode: null,
            confidence,
            transient ? ApplicationCategory.Transient : CategoryOf(composite, name),
            group)
        {
            ExecutablePath = path,
            SignerSubject = composite.SignerSubject,
            SignatureStatus = composite.SignatureStatus,
        };
    }

    /// <summary>An installer, updater or bootstrapper by file name, or anything run from a temp folder.</summary>
    public static bool IsTransient(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (executablePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains(@"\Tmp\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var file = FileName(executablePath)?.ToLowerInvariant();
        return file is not null && TransientMarkers.Any(m => file.Contains(m, StringComparison.Ordinal));
    }

    // ---- shared -------------------------------------------------------------------------

    /// <summary>What to call the application, from what this evidence knows.</summary>
    private static string? DisplayName(SoftwareEvidence evidence) =>
        Value(evidence.Name) ?? FileName(evidence.ExecutablePath);

    /// <summary>
    /// A source that found only an executable still names something: the file.
    /// Better than dropping an application because nothing declared a display
    /// name for it.
    /// </summary>
    private static string? FileName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Value(Path.GetFileNameWithoutExtension(executablePath.Trim()));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What kind of software this is: what the source declared when it knew (a
    /// package manifest says outright that it is a framework), otherwise what
    /// the name suggests.
    /// </summary>
    private static ApplicationCategory CategoryOf(SoftwareEvidence evidence, string name)
    {
        if (evidence.Source == EvidenceSource.RunningProcess)
        {
            return ApplicationCategory.Observed;
        }

        if (evidence.Category is { } declared)
        {
            return declared;
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

    private static string? Value(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string Trimmed(string? value) => value?.Trim() ?? string.Empty;

    private static bool NamesEqual(string? left, string? right) =>
        Value(left) is { } l && Value(right) is { } r && string.Equals(l, r, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One installation as it accumulates evidence. The first record's fields are
    /// the row's fields, exactly as the normalizer's first-wins rule always made
    /// them; later records add evidence and nothing else.
    /// </summary>
    private sealed class Installation
    {
        private readonly SoftwareEvidence _first;
        private readonly List<SoftwareEvidence> _evidence;
        private readonly List<(SoftwareEvidence Evidence, string Path)> _attached = [];
        private string? _adoptedDirectory;
        private bool _adoptionWithdrawn;

        public Installation(ApplicationIdentity identity, string name, SoftwareEvidence first)
        {
            Identity = identity;
            Name = name;
            _first = first;
            _evidence = [first];
            InstallLocation = first.InstallLocation;
            HasRecordedLocation = !string.IsNullOrWhiteSpace(first.InstallLocation);
            UpgradeCode = Value(first.UpgradeCode);
        }

        public ApplicationIdentity Identity { get; }

        public string Name { get; }

        public string? InstallLocation { get; private set; }

        /// <summary>Whether the installation record itself said where the application is.</summary>
        public bool HasRecordedLocation { get; }

        public string? UpgradeCode { get; private set; }

        public bool IsNamed(string? name) => NamesEqual(Name, name);

        /// <summary>The publishers agree, or one of them did not say.</summary>
        public bool PublisherAgrees(SoftwareEvidence item) =>
            Value(_first.Publisher) is null || Value(item.Publisher) is null || NamesEqual(_first.Publisher, item.Publisher);

        /// <summary>Recorded in the same scope, and for the same account when per-user.</summary>
        public bool IsRecordedFor(SoftwareEvidence item) =>
            _first.Scope == item.Scope
            && (_first.Scope != SoftwareScope.User || NamesEqual(_first.InstalledForUser, item.InstalledForUser));

        /// <summary>A second installation record for the same row.</summary>
        public void Absorb(SoftwareEvidence item)
        {
            _evidence.Add(item);
            UpgradeCode ??= Value(item.UpgradeCode);
        }

        /// <summary>Supplementary evidence about an executable inside this installation.</summary>
        public void Attach(SoftwareEvidence item, string path)
        {
            _evidence.Add(item);
            _attached.Add((item, path));
        }

        /// <summary>
        /// Supplementary evidence named like this installation, which has no
        /// recorded directory: take the executable's, if it is one an
        /// application could own and Force Stop could act on safely -- never a
        /// shared folder, never the Windows directory -- and only while every
        /// such piece of evidence agrees.
        /// </summary>
        /// <remarks>
        /// A shortcut is a file a user can write. Whatever it points at, the
        /// directory adopted here becomes the root an operator's Force Stop
        /// terminates processes under, so the rule is the same one an observed
        /// executable's location follows, and it is refused rather than trusted.
        /// </remarks>
        public void Adopt(SoftwareEvidence item, string path)
        {
            Attach(item, path);

            var directory = ExecutablePath.DirectoryOf(path);
            if (directory is null || _adoptionWithdrawn || !ExecutablePath.IsAdoptableLocation(directory))
            {
                return;
            }

            if (_adoptedDirectory is null)
            {
                _adoptedDirectory = directory;
                InstallLocation = directory;
                return;
            }

            if (!string.Equals(_adoptedDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                // Two directories for one application is a disagreement, and a
                // location Force Stop acts on is not something to settle by guessing.
                _adoptionWithdrawn = true;
                _adoptedDirectory = null;
                InstallLocation = null;
            }
        }

        public DiscoveredApplication Build()
        {
            var primary = PrimaryExecutable();

            // The signer: what the installation record itself says first -- a
            // package's publisher is the subject Windows verified its signature
            // against -- and only then what a file's own signature claims.
            var signed = _evidence.FirstOrDefault(e => e.IsAuthoritative && Value(e.SignerSubject) is not null)
                ?? _attached
                    .Select(a => a.Evidence)
                    .Where(e => e.Source == EvidenceSource.ExecutableMetadata)
                    .OrderBy(e => string.Equals(ExecutablePath.Normalize(e.ExecutablePath), primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .FirstOrDefault();

            return new DiscoveredApplication(
                Identity,
                Name,
                _first.Version,
                _first.Publisher,
                _first.InstallDate,
                InstallLocation,
                _first.RegistryView,
                _first.Scope,
                _first.InstalledForUser,
                _first.ProductCode,
                DiscoveryConfidence.Installed,
                CategoryOf(_first, Name),
                _evidence)
            {
                UpgradeCode = UpgradeCode,
                ExecutablePath = primary,
                SignerSubject = Value(signed?.SignerSubject),
                SignatureStatus = Value(signed?.SignatureStatus),
            };
        }

        /// <summary>
        /// The executable that best stands for the application: the one its
        /// installation record declares (a package manifest names its
        /// application's executable), then a shortcut named like it, then an
        /// execution alias, then any shortcut, then a process. Among equals, the
        /// first found.
        /// </summary>
        private string? PrimaryExecutable()
        {
            var declared = _evidence
                .Where(e => e.IsAuthoritative)
                .Select(e => ExecutablePath.Normalize(e.ExecutablePath))
                .FirstOrDefault(p => p is not null);
            if (declared is not null)
            {
                return declared;
            }

            int Rank(SoftwareEvidence e) => e.Source switch
            {
                EvidenceSource.StartMenuShortcut when IsNamed(e.Name) => 0,
                EvidenceSource.AppPaths => 1,
                EvidenceSource.StartMenuShortcut => 2,
                EvidenceSource.RunningProcess => 3,
                _ => 4,
            };

            return _attached.OrderBy(a => Rank(a.Evidence)).Select(a => a.Path).FirstOrDefault();
        }
    }
}
