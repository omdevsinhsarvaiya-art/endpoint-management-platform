namespace EndpointPlatform.Domain.Software;

/// <summary>What a deployment decided to do about one device, and why.</summary>
public enum SoftwareEligibility
{
    /// <summary>The package is not present; install it.</summary>
    InstallRequired = 0,

    /// <summary>An older version is present; install over it.</summary>
    UpdateRequired = 1,

    /// <summary>The requested version is already present. No task.</summary>
    AlreadyInstalled = 2,

    /// <summary>A newer version is present. No task -- this platform does not downgrade.</summary>
    NewerInstalled = 3,

    /// <summary>
    /// Something is installed but its version cannot be ordered against the
    /// package's. No task, and the operator is told rather than the platform
    /// guessing.
    /// </summary>
    VersionNotComparable = 4,

    /// <summary>The device is retired. It receives no tasks of any kind.</summary>
    Retired = 5,

    /// <summary>Outside the administrator's device scope, or not in the organization.</summary>
    NotPermitted = 6,

    /// <summary>
    /// An install of this package is already outstanding on this device. No task.
    /// </summary>
    /// <remarks>
    /// The idempotency guard. A double-clicked Deploy button, a browser retry, or
    /// a client retrying after a network timeout all arrive as a second, entirely
    /// valid-looking request. Without this the device would get two InstallPackage
    /// tasks for the same package and run the installer twice concurrently, which
    /// Windows Installer serialises at best and fails at worst.
    /// </remarks>
    AlreadyInProgress = 7,
}

/// <summary>One installed application, as far as eligibility is concerned.</summary>
/// <param name="ProductCode">The MSI product code, when the installer recorded one.</param>
public sealed record InstalledApplication(
    string Name, string? Version, string? Publisher, string? ProductCode);

/// <summary>The package being deployed, as far as eligibility is concerned.</summary>
public sealed record DeployableSoftware(
    string Name, string Version, string? Publisher, string? MsiProductCode);

/// <summary>
/// Decides whether a device needs a package, from observed inventory.
/// </summary>
/// <remarks>
/// <para>
/// Pure and dependency-free so the matrix that matters -- missing, same, older,
/// newer, unreadable -- is proven with fixtures rather than by deploying to real
/// machines.
/// </para>
/// <para>
/// The purpose is to <b>not</b> create tasks. A deployment that queues an install
/// for every targeted device reinstalls software that is already correct, which
/// on a 350-device fleet is hundreds of pointless MSI executions, each one a real
/// risk of breaking a working installation.
/// </para>
/// </remarks>
public static class SoftwareEligibilityEvaluator
{
    /// <summary>
    /// Evaluates one device's installed software against a package.
    /// </summary>
    /// <param name="installed">
    /// Every application observed on the device. All of them, not a pre-filtered
    /// set: matching is this function's job and doing it in a query would put the
    /// rule in two places.
    /// </param>
    public static SoftwareEligibility Evaluate(
        DeployableSoftware package, IEnumerable<InstalledApplication> installed)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(installed);

        var matches = installed.Where(app => Matches(package, app)).ToList();
        if (matches.Count == 0)
        {
            return SoftwareEligibility.InstallRequired;
        }

        // The same product can legitimately appear more than once -- a machine-wide
        // install plus a per-user one, or one per user. The newest present decides:
        // if anybody already has the requested version there is nothing to do, and
        // if the newest is older then an update is genuinely needed.
        var newestIsSame = false;
        var newestIsNewer = false;
        var comparable = false;
        string? newest = null;

        foreach (var match in matches)
        {
            if (SoftwareVersion.AreSame(match.Version, package.Version))
            {
                newestIsSame = true;
            }

            var ordering = SoftwareVersion.Compare(match.Version, package.Version);
            if (ordering is null)
            {
                continue;
            }

            comparable = true;
            if (newest is null || SoftwareVersion.Compare(match.Version, newest) > 0)
            {
                newest = match.Version;
                newestIsNewer = ordering > 0;
            }
        }

        if (newestIsSame)
        {
            return SoftwareEligibility.AlreadyInstalled;
        }

        if (!comparable)
        {
            // Something is there but nothing can be ordered against it. Installing
            // over it could be a downgrade, so this is reported, not guessed.
            return SoftwareEligibility.VersionNotComparable;
        }

        return newestIsNewer
            ? SoftwareEligibility.NewerInstalled
            : SoftwareEligibility.UpdateRequired;
    }

    /// <summary>Whether an installed application is this package's product.</summary>
    /// <remarks>
    /// The MSI product code is the reliable identity and wins whenever the
    /// installed entry carries one: it survives a renamed display name and
    /// distinguishes two products that share a name. Most software is not MSI
    /// though -- barely half the entries on a real machine have a product code --
    /// so name plus publisher is the fallback. Publisher is only required to match
    /// when both sides declare one, because inventory frequently omits it and a
    /// missing publisher must not silently mean "different product".
    /// </remarks>
    private static bool Matches(DeployableSoftware package, InstalledApplication app)
    {
        if (!string.IsNullOrWhiteSpace(app.ProductCode) && !string.IsNullOrWhiteSpace(package.MsiProductCode))
        {
            return string.Equals(app.ProductCode.Trim(), package.MsiProductCode.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(app.Name?.Trim(), package.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(app.Publisher) || string.IsNullOrWhiteSpace(package.Publisher))
        {
            return true;
        }

        return string.Equals(app.Publisher.Trim(), package.Publisher.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether this outcome means a task should be created.</summary>
    public static bool NeedsInstall(this SoftwareEligibility eligibility) =>
        eligibility is SoftwareEligibility.InstallRequired or SoftwareEligibility.UpdateRequired;
}
