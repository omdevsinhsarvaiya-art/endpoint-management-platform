using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Software;

/// <summary>The mechanism the endpoint uses to remove an application.</summary>
/// <remarks>
/// Serialised by name on the wire and in the task payload. There are exactly
/// two, and both are typed calls into a Windows service: there is no member for
/// running a vendor's uninstaller, because that is a program the agent does not
/// launch (ADR-0005).
/// </remarks>
public enum ApplicationRemovalMethod
{
    /// <summary>A Windows Installer product, removed by product code through msi.dll.</summary>
    WindowsInstaller = 0,

    /// <summary>An MSIX/AppX package, removed by full name through the deployment engine.</summary>
    Package = 1,
}

/// <summary>Why an installed application cannot be removed by this platform.</summary>
public enum NotRemovableReason
{
    /// <summary>
    /// A Windows Installer product installed for one account. The agent runs as
    /// SYSTEM, and msi.dll in that context cannot see another account's
    /// per-user product, let alone remove it.
    /// </summary>
    PerUserInstall = 0,

    /// <summary>A package that is part of the operating system.</summary>
    SystemComponent = 1,

    /// <summary>The endpoint agent itself. Refused here and again on the endpoint.</summary>
    ProtectedAgent = 2,

    /// <summary>
    /// Nothing typed to hand to Windows: an EXE-installer registration, an
    /// observed executable, or a row from an agent too old to report identity.
    /// Its uninstaller, where one exists, is a program the agent does not launch.
    /// </summary>
    NoInstallerIdentity = 3,
}

/// <summary>
/// Whether one inventory row can be removed by the platform, and how.
/// </summary>
/// <remarks>
/// <para>
/// Pure and dependency-free, so every row of the removability table is proven
/// with fixtures rather than by uninstalling things from real machines. The
/// server asks it before queueing; the console asks a mirror of it before
/// offering the action; the endpoint re-checks the identity it is handed
/// against Windows and its own self-protection before acting. Each side only
/// ever refuses, so a divergence cannot fail open.
/// </para>
/// <para>
/// The honest boundary: only a machine-wide Windows Installer product and a
/// removable MSIX package have an identity Windows will act on through a typed
/// call. Everything else is reported as not removable with the reason, rather
/// than guessed at.
/// </para>
/// </remarks>
/// <param name="Removable">Whether a task can be queued for this row.</param>
/// <param name="Method">How the endpoint removes it, when it can.</param>
/// <param name="Reason">Why it cannot, when it cannot.</param>
public sealed record SoftwareRemovability(
    bool Removable, ApplicationRemovalMethod? Method, NotRemovableReason? Reason)
{
    /// <summary>The agent's own display name, as its installer registers it.</summary>
    public const string AgentProductName = "Endpoint Platform Agent";

    /// <summary>
    /// The agent's Windows Installer upgrade code. Stable across every release,
    /// which is what makes it the identity to refuse rather than a product code
    /// that changes with each build.
    /// </summary>
    public const string AgentUpgradeCode = "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}";

    /// <summary>The publisher id every Windows inbox package carries.</summary>
    public const string WindowsInboxPublisherId = "cw5n1h2txyewy";

    /// <summary>Package categories that are operating-system parts, whatever their publisher.</summary>
    private static readonly string[] SystemPackageCategories =
        ["InboxApp", "FrameworkOrResource", "Component"];

    private static readonly SoftwareRemovability ByWindowsInstaller =
        new(true, ApplicationRemovalMethod.WindowsInstaller, null);

    private static readonly SoftwareRemovability ByPackage =
        new(true, ApplicationRemovalMethod.Package, null);

    /// <summary>Decides removability for one inventory row.</summary>
    public static SoftwareRemovability Evaluate(DeviceSoftware row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Self-protection first, before any identity is considered: an agent
        // that removes itself leaves a machine nobody can manage, and the row's
        // identity is exactly what would make that succeed.
        if (IsAgent(row))
        {
            return Refuse(NotRemovableReason.ProtectedAgent);
        }

        if (IsWindowsInstallerProduct(row))
        {
            // msi.dll runs in the SYSTEM context on the endpoint and does not see
            // a product registered in another account's profile.
            return string.Equals(row.InstallationScope, "Machine", StringComparison.OrdinalIgnoreCase)
                ? ByWindowsInstaller
                : Refuse(NotRemovableReason.PerUserInstall);
        }

        if (IsPackage(row))
        {
            return IsSystemPackage(row)
                ? Refuse(NotRemovableReason.SystemComponent)
                : ByPackage;
        }

        // Registered (EXE installer), Executable, Observed, or a row an agent
        // older than 1.9.0 reported with no identity at all.
        return Refuse(NotRemovableReason.NoInstallerIdentity);
    }

    private static SoftwareRemovability Refuse(NotRemovableReason reason) => new(false, null, reason);

    /// <summary>
    /// The agent by name or by upgrade code. The third rule in the table -- a
    /// product whose install directory is the agent's -- needs the agent's actual
    /// directory, which only the endpoint knows; it enforces that one itself.
    /// </summary>
    private static bool IsAgent(DeviceSoftware row) =>
        string.Equals(row.Name?.Trim(), AgentProductName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(row.UpgradeCode?.Trim(), AgentUpgradeCode, StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsInstallerProduct(DeviceSoftware row) =>
        string.Equals(row.IdentityKind, "WindowsInstaller", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(row.ProductCode);

    private static bool IsPackage(DeviceSoftware row) =>
        string.Equals(row.IdentityKind, "Package", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(row.PackageFullName);

    private static bool IsSystemPackage(DeviceSoftware row) =>
        (row.Category is not null
            && SystemPackageCategories.Contains(row.Category, StringComparer.Ordinal))
        || HasPublisherId(row.PackageFamilyName, WindowsInboxPublisherId)
        || HasPublisherId(row.PackageFullName, WindowsInboxPublisherId);

    /// <summary>
    /// Both a family name (<c>Name_PublisherId</c>) and a full name
    /// (<c>Name_Version_Arch_ResourceId_PublisherId</c>) end in the publisher id.
    /// </summary>
    private static bool HasPublisherId(string? packageName, string publisherId)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var cut = packageName.LastIndexOf('_');
        return cut >= 0
            && string.Equals(packageName[(cut + 1)..], publisherId, StringComparison.OrdinalIgnoreCase);
    }
}
