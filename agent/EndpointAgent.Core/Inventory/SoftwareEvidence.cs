namespace EndpointAgent.Core.Inventory;

/// <summary>
/// How a discovery source came to know an application exists.
/// </summary>
/// <remarks>
/// <para>
/// Loosely ordered by authority. The first three are <em>authoritative</em> -- an
/// actual installation record: Windows Installer, an uninstall registration, or a
/// package registration. The rest are <em>supplementary</em> -- a hint the
/// endpoint offered (an execution alias, a shortcut, a file's own metadata, a
/// process running now). Supplementary evidence sharpens identity and location
/// but, on its own, never makes an application "Installed": see
/// <see cref="DiscoveryConfidence"/>.
/// </para>
/// <para>
/// This is the vocabulary for answering the one question the whole subsystem
/// exists to answer -- "why does Techsara believe this application exists?" -- so
/// the values are named for what an administrator would recognise, not for the
/// registry key or API that produced them.
/// </para>
/// </remarks>
public enum EvidenceSource
{
    /// <summary>A Windows Installer product (an uninstall key named by ProductCode, or the Installer database).</summary>
    WindowsInstaller,

    /// <summary>An uninstall registration that is not a Windows Installer product (a traditional EXE installer).</summary>
    UninstallRegistry,

    /// <summary>An MSIX/AppX/Store package registration (the AppModel repository plus the package manifest).</summary>
    PackageRegistration,

    /// <summary>An <c>App Paths</c> execution alias. Supplementary: a location hint, never an installation record.</summary>
    AppPaths,

    /// <summary>A Start Menu shortcut resolving to an executable. Supplementary.</summary>
    StartMenuShortcut,

    /// <summary>A primary executable's own version information and Authenticode signature. Supplementary.</summary>
    ExecutableMetadata,

    /// <summary>A process running now. Supplementary: presence, not installation.</summary>
    RunningProcess,
}

/// <summary>
/// One thing one discovery source found, before anything is merged.
/// </summary>
/// <remarks>
/// <para>
/// A plain record with no Windows types, exactly like <see cref="DiscoveredSoftware"/>:
/// enumerating the registry or the package store needs a real machine, but deciding
/// what counts as the same application, and with what confidence, does not -- and
/// that decision is where the bugs live, so it is exercised with fixtures.
/// </para>
/// <para>
/// Every field but <see cref="Source"/> is optional. A source reports only what it
/// itself knows: a running process knows an executable path and maybe a package
/// full name; an uninstall key knows a display name and version. A field a source
/// cannot see is null, never guessed -- the same rule the security-posture contract
/// follows.
/// </para>
/// </remarks>
public sealed record SoftwareEvidence(
    EvidenceSource Source,
    string? Name = null,
    string? Version = null,
    string? Publisher = null,
    string? InstallDate = null,
    string? InstallLocation = null,
    string? RegistryView = null,
    SoftwareScope Scope = SoftwareScope.Machine,
    string? InstalledForUser = null,
    string? ProductCode = null,
    string? UpgradeCode = null,
    string? PackageFamilyName = null,
    string? PackageFullName = null,
    string? ExecutablePath = null,
    string? SignerSubject = null,
    string? SignatureStatus = null,
    string? FileDescription = null,
    ApplicationCategory? Category = null)
{
    /// <summary>
    /// Whether this evidence is an installation record rather than a hint.
    /// </summary>
    /// <remarks>
    /// The line the whole confidence model rests on: an application is "Installed"
    /// only when at least one authoritative source vouches for it. An App Path or a
    /// running process, however clear, is not by itself an installation.
    /// </remarks>
    public bool IsAuthoritative =>
        Source is EvidenceSource.WindowsInstaller
            or EvidenceSource.UninstallRegistry
            or EvidenceSource.PackageRegistration;
}
