namespace EndpointAgent.Core.Inventory;

/// <summary>What kind of thing identifies an application, and therefore how stably.</summary>
public enum IdentityKind
{
    /// <summary>An MSIX/AppX/Store package. Identified by package family name.</summary>
    Package,

    /// <summary>A Windows Installer product. Identified by upgrade code, or product code when it has none.</summary>
    WindowsInstaller,

    /// <summary>An application with an uninstall registration but no installer identity of its own.</summary>
    Registered,

    /// <summary>An executable with no installation record at all -- portable, or running only.</summary>
    Executable,
}

/// <summary>How sure the platform is that an application is present, and on what basis.</summary>
/// <remarks>
/// Derived from evidence, never asserted by a source. A source reports what it saw;
/// the merger decides what that adds up to.
/// </remarks>
public enum DiscoveryConfidence
{
    /// <summary>At least one authoritative source holds an installation record.</summary>
    Installed,

    /// <summary>Running now, with no installation record. A portable application looks like this.</summary>
    Observed,

    /// <summary>A shortcut or execution alias points at it, but nothing confirms an installation.</summary>
    Referenced,

    /// <summary>An installer, updater, or temporary execution. Present, but not an installed application.</summary>
    Transient,
}

/// <summary>
/// What kind of software this is, decided on the endpoint so the console can filter
/// without having to re-derive it.
/// </summary>
/// <remarks>
/// Classification never drops anything: security wants the data even when an
/// administrator does not want it in the default view. Everything is collected and
/// categorised; what the console shows by default is a presentation choice made
/// against these categories.
/// </remarks>
public enum ApplicationCategory
{
    /// <summary>A user-facing application. The default view.</summary>
    Application,

    /// <summary>An application that ships with Windows (Notepad, Paint, Terminal).</summary>
    InboxApp,

    /// <summary>A runtime, redistributable or SDK: present for other software, not used directly.</summary>
    RuntimeOrSdk,

    /// <summary>A package framework or resource package. Never a thing an administrator manages.</summary>
    FrameworkOrResource,

    /// <summary>A system component, servicing package or compatibility database.</summary>
    Component,

    /// <summary>Running with no installation record.</summary>
    Observed,

    /// <summary>An installer, updater or temporary execution.</summary>
    Transient,
}

/// <summary>
/// What makes an application <em>that</em> application, across versions and across
/// discovery sources.
/// </summary>
/// <remarks>
/// <para>
/// A display name is not an identity. Two publishers ship a "Setup"; one product
/// is called four different things by its uninstall key, its manifest, its
/// shortcut and its file metadata; and a portable <c>slack.exe</c> in a downloads
/// folder is not Slack. Every later decision -- deduplication, whether an update
/// replaced a row, and eventually which application a block rule names -- reads
/// <see cref="StableKey"/>, never the name.
/// </para>
/// <para>
/// <see cref="StableKey"/> survives an update; <see cref="VersionKey"/> is the
/// thing that changed. For a package that is family name versus full name; for a
/// Windows Installer product, upgrade code versus product code. Splitting them is
/// what lets an update be recognised as the same application rather than a second
/// one.
/// </para>
/// </remarks>
public sealed record ApplicationIdentity(IdentityKind Kind, string StableKey, string? VersionKey = null)
{
    /// <summary>
    /// A character no name, publisher or path can contain, joining the parts of a
    /// composite key so ("A", "B|C") and ("A|B", "C") cannot collide.
    /// </summary>
    /// <remarks>
    /// The same ASCII Unit Separator, and the same reasoning, as
    /// <see cref="SoftwareInventoryNormalizer"/>: a collision here would silently
    /// merge two applications into one row.
    /// </remarks>
    private const char KeySeparator = (char)0x1F;

    /// <summary>
    /// Derives the identity a piece of evidence implies, strongest form first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered by how well the identifier survives: a package family name is
    /// assigned by Windows and never changes; an upgrade code is chosen by the
    /// vendor and is meant to outlive versions; a product code changes every
    /// release but still names one product; below that there is only what the
    /// machine can be observed to hold -- who signed it or published it, what it
    /// calls itself, and where it lives.
    /// </para>
    /// <para>
    /// Returns null when there is not enough to identify anything, which is a
    /// supported answer: evidence that identifies nothing is evidence about
    /// nothing, and the merger drops it rather than inventing a row.
    /// </para>
    /// </remarks>
    public static ApplicationIdentity? Derive(SoftwareEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (Value(evidence.PackageFamilyName) is { } family)
        {
            return new ApplicationIdentity(IdentityKind.Package, Normalize(family), Value(evidence.PackageFullName));
        }

        if (Value(evidence.UpgradeCode) is { } upgrade)
        {
            return new ApplicationIdentity(
                IdentityKind.WindowsInstaller, Normalize(upgrade), Value(evidence.ProductCode) ?? Value(evidence.Version));
        }

        if (Value(evidence.ProductCode) is { } product)
        {
            // No upgrade code recorded. The product code still names one product;
            // it just does not survive the next release, so an update will read as
            // a new application until an upgrade code is available for it.
            return new ApplicationIdentity(IdentityKind.WindowsInstaller, Normalize(product), Value(evidence.Version));
        }

        // Below the installer identifiers, identity is composed from what can be
        // observed: who vouches for it, what it calls itself, and where it lives.
        // The publisher is preferred as the signer subject when one was verified,
        // because a signature is checkable and a registry string is not.
        var authority = Value(evidence.SignerSubject) ?? Value(evidence.Publisher);
        var name = Value(evidence.Name);
        var place = Value(evidence.InstallLocation) ?? DirectoryOf(evidence.ExecutablePath);

        if (name is null && place is null)
        {
            return null;
        }

        var kind = evidence.IsAuthoritative ? IdentityKind.Registered : IdentityKind.Executable;
        var key = string.Join(
            KeySeparator, Normalize(authority ?? string.Empty), Normalize(name ?? string.Empty), Normalize(place ?? string.Empty));

        return new ApplicationIdentity(kind, key, Value(evidence.Version));
    }

    /// <summary>The directory an executable path sits in, or null when there is no usable path.</summary>
    private static string? DirectoryOf(string? executablePath)
    {
        var value = Value(executablePath);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Value(Path.GetDirectoryName(value));
        }
        catch (ArgumentException)
        {
            // A path shape this cannot reason about is no worse than no path.
            return null;
        }
    }

    /// <summary>Trims, and treats blank as absent.</summary>
    private static string? Value(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// The comparable form of one key part: case- and separator-insensitive, so the
    /// same directory written two ways is one key.
    /// </summary>
    private static string Normalize(string raw) =>
        raw.Trim().Trim('"').TrimEnd('\\', '/').ToLowerInvariant();
}
