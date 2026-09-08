using System.Runtime.Versioning;
using System.Security;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads MSIX/AppX package registrations: the Store apps, the sideloaded and
/// developer-signed packages, the packaged desktop applications such as Slack,
/// and the applications Windows itself ships as packages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authoritative.</b> A package registration is Windows' own record that a
/// package is deployed for an account, so what this finds is Installed. Before
/// this source, a packaged application wrote no uninstall key and was invisible
/// to the inventory however plainly it was installed and running.
/// </para>
/// <para>
/// Read from the registry and the file system only. Per-user registrations live
/// in each loaded profile's <c>_Classes</c> hive under the AppModel repository;
/// machine-wide ones under the same path in HKLM. Each registration names the
/// package root, whose <c>AppxManifest.xml</c> is the package's own statement
/// of identity, verified by Windows against the package signature at
/// deployment. No package API, no WinRT projection and no PowerShell is
/// involved, which keeps the agent's target framework and ADR-0005 as they are.
/// </para>
/// <para>
/// <b>Superseded registrations.</b> The repository keeps registrations for
/// package versions that a later version has replaced; on the reference machine
/// 36 of 182 were such. The state repository's package index lists only what is
/// actually deployed, and is the primary filter; where it cannot be read, a
/// registration whose package root holds a manifest for a different version is
/// treated as superseded. The two rules agreed exactly on the reference machine.
/// </para>
/// <para>
/// Framework and resource packages, and the packages under the Windows
/// directory, are reported and categorised -- never dropped. What the console
/// shows by default is a presentation choice made against those categories.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPackageRegistrationEvidenceSource(ILogger<WindowsPackageRegistrationEvidenceSource> logger)
    : ISoftwareEvidenceSource
{
    private readonly ILogger<WindowsPackageRegistrationEvidenceSource> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private const string RepositoryPackages =
        @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const string MachineRepositoryPackages = @"SOFTWARE\Classes\" + RepositoryPackages;

    private const string StateRepositoryIndex =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateRepository\Cache\Package\Index\PackageFullName";

    private const string ManifestFileName = "AppxManifest.xml";

    /// <summary>A hive registering more packages than this is not a real profile.</summary>
    internal const int MaxPackagesPerHive = 5_000;

    private const int MaxIndexEntries = 50_000;

    /// <inheritdoc />
    public string SourceName => "PackageRegistration";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = new List<SoftwareEvidence>();
        var deployed = ReadDeployedIndex();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var machine = hklm.OpenSubKey(MachineRepositoryPackages);
            ReadRepository(machine, SoftwareScope.Machine, null, deployed, windows, evidence, cancellationToken);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not read the machine package repository; machine-wide packages are not reported.");
        }

        foreach (var (sid, account) in WindowsUserHives.Loaded())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var packages = WindowsUserHives.OpenUserSubKey(sid, RepositoryPackages, classes: true);
            ReadRepository(packages, SoftwareScope.User, account, deployed, windows, evidence, cancellationToken);
        }

        return ValueTask.FromResult<IReadOnlyList<SoftwareEvidence>>(evidence);
    }

    private void ReadRepository(
        RegistryKey? packages,
        SoftwareScope scope,
        string? account,
        HashSet<string>? deployed,
        string windowsDirectory,
        List<SoftwareEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (packages is null)
        {
            return;
        }

        string[] names;
        try
        {
            names = packages.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not enumerate package registrations for {Scope}/{Account}.", scope, account);
            return;
        }

        if (names.Length > MaxPackagesPerHive)
        {
            _logger.LogWarning("{Count} package registrations for {Scope}/{Account}; only the first {Max} are read.", names.Length, scope, account, MaxPackagesPerHive);
        }

        var reported = 0;
        var superseded = 0;
        var missing = 0;

        foreach (var fullName in names.Take(MaxPackagesPerHive))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parts = AppxManifest.FullNameParts(fullName);
            if (parts is null)
            {
                continue;
            }

            string? root;
            string? registeredDisplayName;
            try
            {
                using var entry = packages.OpenSubKey(fullName);
                if (entry is null)
                {
                    continue;
                }

                root = ExecutablePath.Normalize(entry.GetValue("PackageRootFolder") as string);
                registeredDisplayName = entry.GetValue("DisplayName") as string;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (root is null || !Directory.Exists(root))
            {
                // Registered, but nothing is there: whatever this was, it is not
                // installed now.
                missing++;
                continue;
            }

            var manifest = ReadManifest(root);
            if (!IsDeployed(fullName, parts[1], manifest, deployed))
            {
                superseded++;
                continue;
            }

            evidence.Add(Describe(fullName, parts, root, registeredDisplayName, manifest, scope, account, windowsDirectory));
            reported++;
        }

        _logger.LogDebug(
            "Package registrations for {Scope}/{Account}: {Reported} reported, {Superseded} superseded, {Missing} without a package root.",
            scope, account, reported, superseded, missing);
    }

    /// <summary>
    /// Whether a registration is for the version actually deployed.
    /// </summary>
    /// <remarks>
    /// The state repository's index is authoritative when it can be read. Without
    /// it, a package root that holds a manifest for another version has been
    /// taken over by that version; a root with no manifest at all cannot say, and
    /// is reported rather than hidden.
    /// </remarks>
    internal static bool IsDeployed(string fullName, string version, AppxManifest? manifest, HashSet<string>? deployed)
    {
        if (deployed is not null)
        {
            return deployed.Contains(fullName);
        }

        return manifest?.Version is null || string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase);
    }

    private static SoftwareEvidence Describe(
        string fullName,
        string[] parts,
        string root,
        string? registeredDisplayName,
        AppxManifest? manifest,
        SoftwareScope scope,
        string? account,
        string windowsDirectory)
    {
        // What to call it: the manifest's literal name; else the repository's
        // display name, resolved when it is an indirect string or a bare
        // resource reference; else the identity name, which is at least stable.
        var name = manifest?.DisplayName
            ?? WindowsIndirectString.Resolve(registeredDisplayName)
            ?? WindowsIndirectString.ResolvePackageResource(fullName, registeredDisplayName)
            ?? manifest?.Name
            ?? parts[0];

        var publisher = manifest?.PublisherDisplayName ?? AppxManifest.CommonNameOf(manifest?.Publisher);

        string? executable = null;
        if (manifest?.Executable is { } relative)
        {
            var candidate = ExecutablePath.Normalize(Path.Combine(root, relative));
            executable = candidate is not null && ExecutablePath.IsUnder(candidate, root) ? candidate : null;
        }

        ApplicationCategory? category = null;
        if (manifest is { IsFramework: true } or { IsResourcePackage: true })
        {
            category = ApplicationCategory.FrameworkOrResource;
        }
        else if (ExecutablePath.IsUnder(root, windowsDirectory))
        {
            category = ApplicationCategory.InboxApp;
        }

        return new SoftwareEvidence(
            EvidenceSource.PackageRegistration,
            name,
            manifest?.Version ?? parts[1],
            publisher,
            InstallDate: null,
            InstallLocation: root,
            RegistryView: null,
            scope,
            account,
            PackageFamilyName: AppxManifest.FamilyNameOf(fullName),
            PackageFullName: fullName,
            ExecutablePath: executable,
            // The manifest publisher is the subject Windows verified the package
            // signature against at deployment: a checked identity, not a claim.
            SignerSubject: manifest?.Publisher,
            SignatureStatus: manifest?.Publisher is null ? null : ExecutableSignatureStatus.Signed.ToString(),
            Category: category);
    }

    private AppxManifest? ReadManifest(string root)
    {
        var path = Path.Combine(root, ManifestFileName);

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > AppxManifest.MaxBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return AppxManifest.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not read the package manifest under {Root}.", root);
            return null;
        }
    }

    /// <summary>The full names of every package the state repository holds, or null when it cannot be read.</summary>
    private HashSet<string>? ReadDeployedIndex()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var index = hklm.OpenSubKey(StateRepositoryIndex);
            if (index is null)
            {
                _logger.LogDebug("The state repository package index is absent; superseded registrations are told apart by manifest version.");
                return null;
            }

            var names = index.GetSubKeyNames();
            if (names.Length > MaxIndexEntries)
            {
                _logger.LogWarning("The state repository package index holds {Count} entries; it is not used.", names.Length);
                return null;
            }

            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "The state repository package index could not be read; superseded registrations are told apart by manifest version.");
            return null;
        }
    }
}
