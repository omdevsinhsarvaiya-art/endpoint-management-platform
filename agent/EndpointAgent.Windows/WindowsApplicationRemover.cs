using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Msi = EndpointAgent.Windows.WindowsMsiPackageInstaller.NativeMethods;

namespace EndpointAgent.Windows;

/// <summary>
/// Removes applications through the Windows Installer service (<c>msi.dll</c>)
/// and the AppX deployment engine (<see cref="WindowsPackageManager"/>).
/// </summary>
/// <remarks>
/// <para>
/// No process is launched and no shell is invoked (ADR-0005): a Windows
/// Installer product is removed by <c>MsiConfigureProductEx</c> with
/// <c>INSTALLSTATE_ABSENT</c>, a package by the deployment engine's
/// <c>RemovePackageWithOptionsAsync</c>. Both are typed calls carried out by a
/// Windows service; the agent composes no command line. What it cannot do is
/// as deliberate: an EXE installer's uninstaller is a program, and running one
/// is exactly what ADR-0005 forbids, so such applications are not removable
/// here and the server never asks.
/// </para>
/// <para>
/// <b>Self-protection is decided here, not upstream.</b> The agent's product
/// code changes with every build, so it cannot be refused by a constant: a
/// product is the agent when it belongs to the agent's upgrade code, carries the
/// agent's product name, or is installed in the agent's own directory or the
/// vendor folder that holds it. The upgrade-code question is the primary one and
/// it fails closed: when Windows Installer cannot finish the enumeration that
/// answers it, the removal is refused rather than attempted, because "could not
/// tell" is not "not the agent". A package is refused when its publisher is
/// Windows' own or its registered root lies under the Windows directory. These
/// checks run before the removal call, on what the machine says rather than on
/// what the task says, because an agent that removed itself would leave the
/// device unmanaged and the task unreported.
/// </para>
/// <para>
/// Reboots are suppressed; a removal that wants one is reported, never
/// performed. Removal is idempotent: a product or package that is already gone
/// is the desired state and is reported as such, and one the service claims to
/// have removed is checked to be gone before that claim is repeated. Presence
/// is the machine's own record in both halves -- Windows Installer's product
/// state, the package repositories and the state repository index -- because
/// the deployment engine completes a removal of a package nobody has as a
/// success, which would otherwise become "removed" for something that was
/// never there.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationRemover(
    ILogger<WindowsApplicationRemover> logger,
    string? agentDirectory = null) : IApplicationRemover
{
    /// <summary>The agent's upgrade code (Package.wxs): the identity that survives every build.</summary>
    internal const string AgentUpgradeCode = "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}";

    /// <summary>The agent's product name (Package.wxs).</summary>
    internal const string AgentProductName = "Endpoint Platform Agent";

    /// <summary>Publisher id of the packages Windows itself ships.</summary>
    internal const string WindowsInboxPublisherId = "cw5n1h2txyewy";

    /// <summary>How long the deployment engine is given before a removal is reported as failed.</summary>
    internal static readonly TimeSpan PackageRemovalTimeout = TimeSpan.FromMinutes(10);

    private const int InstallStateDefault = 5;       // INSTALLSTATE_DEFAULT: installed and usable.
    private const int InstallStateAbsent = 2;        // INSTALLSTATE_ABSENT: remove.
    private const int InstallLevelDefault = 0;       // INSTALLLEVEL_DEFAULT
    private const uint InstallUiLevelNone = 2;       // INSTALLUILEVEL_NONE

    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;

    /// <summary>The end of an enumeration -- and the only status that means "not related".</summary>
    private const uint ErrorNoMoreItems = 259;

    private const uint ErrorUnknownProduct = 1605;
    private const uint ErrorProductUninstalled = 1614;
    private const uint ErrorInstallAlreadyRunning = 1618;
    private const uint ErrorSuccessRebootInitiated = 1641;
    private const uint ErrorSuccessRebootRequired = 3010;

    private const int MaxRelatedProducts = 64;
    private const int MaxErrorTextLength = 200;

    // Registry writes trail the engine's completion by a moment; a bounded wait
    // keeps a real removal from being reported as a failure.
    private const int PostVerifyAttempts = 4;
    private static readonly TimeSpan PostVerifyInterval = TimeSpan.FromMilliseconds(250);

    private const string RepositoryPackages =
        @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const string MachineRepositoryPackages = @"SOFTWARE\Classes\" + RepositoryPackages;

    /// <summary>Every package deployed on the machine, for any user, by full name.</summary>
    private const string StateRepositoryIndex =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateRepository\Cache\Package\Index\PackageFullName";

    private const string RefusedAsAgent = "refused: this is the endpoint agent";
    private const string RefusedAsAgentDirectory = "refused: the product is installed in the endpoint agent's directory";
    private const string RefusedAsSystemComponent = "refused: the package is an operating-system component";

    /// <summary>
    /// What is said when the upgrade-code check could not run. Self-protection
    /// that cannot answer must refuse, not wave the product through.
    /// </summary>
    private const string RefusedAsUnconfirmed =
        "refused: Windows Installer could not confirm the product's identity";

    private readonly ILogger<WindowsApplicationRemover> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private readonly string _agentDirectory = (agentDirectory ?? AppContext.BaseDirectory).TrimEnd('\\');

    // ------------------------------------------------------------ Windows Installer

    public ValueTask<ApplicationRemovalOutcome> RemoveWindowsInstallerProductAsync(
        string productCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsProductCode(productCode))
        {
            return ValueTask.FromResult(new ApplicationRemovalOutcome(
                ApplicationRemovalResult.Refused, null, "refused: the product code is not a Windows Installer product code"));
        }

        return ValueTask.FromResult(RemoveProduct(productCode.Trim().ToUpperInvariant()));
    }

    private ApplicationRemovalOutcome RemoveProduct(string productCode)
    {
        try
        {
            var refusal = ProtectionRefusal(productCode);
            if (refusal is not null)
            {
                _logger.LogWarning("Refused to remove product {ProductCode}: {Reason}.", productCode, refusal);
                return new(ApplicationRemovalResult.Refused, null, refusal);
            }

            if (Msi.MsiQueryProductState(productCode) != InstallStateDefault)
            {
                return new(ApplicationRemovalResult.AlreadyRemoved, null, "the product is not installed on this device");
            }

            // Quiet, no UI. The service runs as LocalSystem; MSI requires elevation.
            _ = Msi.MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);

            var code = Msi.MsiConfigureProductEx(
                productCode, InstallLevelDefault, InstallStateAbsent, "REBOOT=ReallySuppress");
            return MapInstallerResult(productCode, code);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Windows Installer is unavailable on this host.");
            return new(ApplicationRemovalResult.Failed, null, "Windows Installer is unavailable on this host");
        }
    }

    private ApplicationRemovalOutcome MapInstallerResult(string productCode, uint code)
    {
        switch (code)
        {
            case ErrorSuccess:
                // The installer's word is checked against the machine before it
                // is repeated: a success that leaves the product registered is a
                // failure with a better-sounding name.
                if (Msi.MsiQueryProductState(productCode) == InstallStateDefault)
                {
                    return new(ApplicationRemovalResult.Failed, code,
                        "Windows Installer reported success but the product is still registered");
                }

                _logger.LogWarning("Product {ProductCode} removed by an authorized task.", productCode);
                return new(ApplicationRemovalResult.Removed, code, "removed");

            case ErrorSuccessRebootRequired:
            case ErrorSuccessRebootInitiated:
                // REBOOT=ReallySuppress means 1641 cannot actually have restarted
                // anything; either way the product is gone and a restart finishes
                // the job. Reported, never performed.
                _logger.LogWarning(
                    "Product {ProductCode} removed by an authorized task; a restart is required to finish.", productCode);
                return new(ApplicationRemovalResult.RemovedRebootRequired, code, "removed; a restart is required to finish");

            case ErrorUnknownProduct:
            case ErrorProductUninstalled:
                return Msi.MsiQueryProductState(productCode) == InstallStateDefault
                    ? new(ApplicationRemovalResult.Failed, code, $"Windows Installer returned {code}")
                    : new(ApplicationRemovalResult.AlreadyRemoved, code, "the product is not installed on this device");

            case ErrorInstallAlreadyRunning:
                return new(ApplicationRemovalResult.Failed, code, "another installation is in progress");

            default:
                return new(ApplicationRemovalResult.Failed, code, $"Windows Installer returned {code}");
        }
    }

    /// <summary>Why the product must not be removed, or null when it may be.</summary>
    private string? ProtectionRefusal(string productCode)
    {
        var membership = AgentUpgradeCodeMembership(productCode);
        if (membership is not UpgradeCodeMembership.NotAgent)
        {
            // Neither branch needs the product's registered properties: it is
            // the agent, or nobody can say it is not.
            return ProtectedProductRefusal(null, null, _agentDirectory, membership);
        }

        var name = ReadProductProperty(productCode, "ProductName")
            ?? ReadProductProperty(productCode, "InstalledProductName");
        var location = ReadProductProperty(productCode, "InstallLocation");

        return ProtectedProductRefusal(name, location, _agentDirectory, membership);
    }

    /// <summary>
    /// The pure rule: why a product, by what Windows Installer says of its
    /// upgrade code and by its registered name and install location, is the
    /// agent -- or null when it is not.
    /// </summary>
    internal static string? ProtectedProductRefusal(
        string? productName,
        string? installLocation,
        string agentDirectory,
        UpgradeCodeMembership membership = UpgradeCodeMembership.NotAgent)
    {
        if (membership is UpgradeCodeMembership.Agent)
        {
            return RefusedAsAgent;
        }

        if (membership is UpgradeCodeMembership.Unconfirmed)
        {
            return RefusedAsUnconfirmed;
        }

        if (string.Equals(productName?.Trim(), AgentProductName, StringComparison.OrdinalIgnoreCase))
        {
            return RefusedAsAgent;
        }

        return IsAgentDirectory(installLocation, agentDirectory) ? RefusedAsAgentDirectory : null;
    }

    /// <summary>
    /// Whether a directory is the agent's own, one inside it, or the vendor
    /// folder that holds it.
    /// </summary>
    /// <remarks>
    /// The rule is bounded on purpose. Protecting <em>every</em> ancestor reads
    /// as caution but refuses real products: a driver or utility that registers
    /// its <c>InstallLocation</c> as <c>C:\Program Files</c> or <c>C:\</c> --
    /// and they do -- would be permanently unremovable, with a reason that
    /// claims it is installed in the agent's directory when it is not. What the
    /// directory rule is actually for is the agent's own files and the
    /// installer's vendor folder around them; beyond that, the upgrade code and
    /// the product name are the identity checks, and they do not depend on
    /// where anything sits.
    /// </remarks>
    private static bool IsAgentDirectory(string? candidate, string agentDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var directory = candidate.Trim().Trim('"').TrimEnd('\\', '/');
        var agent = agentDirectory.Trim().Trim('"').TrimEnd('\\', '/');
        if (directory.Length == 0 || agent.Length == 0)
        {
            return false;
        }

        if (string.Equals(directory, agent, StringComparison.OrdinalIgnoreCase)
            || ExecutablePath.IsUnder(directory, agent))
        {
            return true;
        }

        var vendor = VendorDirectoryOf(agent);
        return vendor is not null && string.Equals(directory, vendor, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folder the agent's installer owns around the agent -- its immediate
    /// parent -- or null when there is no such folder.
    /// </summary>
    /// <remarks>
    /// The agent installs into <c>&lt;Program Files&gt;\EndpointPlatform\Agent</c>,
    /// so the vendor folder is <c>EndpointPlatform</c>: a directory the agent's
    /// own package created and nothing else has business being registered to. A
    /// drive root is not that, whatever the layout, so it is excluded rather
    /// than protected by accident on an agent installed at <c>C:\Agent</c>.
    /// </remarks>
    private static string? VendorDirectoryOf(string agentDirectory)
    {
        var separator = agentDirectory.LastIndexOf('\\');
        if (separator <= 0)
        {
            return null;
        }

        var parent = agentDirectory[..separator].TrimEnd('\\');

        // "C:" is what a drive root trims down to: everything on the volume
        // lives under it, so it names nothing in particular.
        return parent.Contains('\\', StringComparison.Ordinal) ? parent : null;
    }

    /// <summary>
    /// What Windows Installer was able to say about the product's membership of
    /// the agent's upgrade code.
    /// </summary>
    internal enum UpgradeCodeMembership
    {
        /// <summary>Windows Installer lists the product under the agent's upgrade code.</summary>
        Agent,

        /// <summary>The enumeration ran to its end and the product was not in it.</summary>
        NotAgent,

        /// <summary>Windows Installer could not answer, so membership is unknown.</summary>
        Unconfirmed,
    }

    /// <summary>
    /// Whether Windows Installer lists the product under the agent's upgrade code.
    /// The reverse lookup is the documented one: <c>MsiGetProductInfo</c> has no
    /// upgrade-code attribute.
    /// </summary>
    private UpgradeCodeMembership AgentUpgradeCodeMembership(string productCode) =>
        RelatedProductMembership(productCode, index =>
        {
            var buffer = new StringBuilder(64);
            var status = Msi.MsiEnumRelatedProducts(AgentUpgradeCode, 0, index, buffer);
            if (status is not ErrorSuccess and not ErrorNoMoreItems)
            {
                _logger.LogWarning(
                    "MsiEnumRelatedProducts returned {Status} for the agent's upgrade code; identity cannot be confirmed.",
                    status);
            }

            return (status, status == ErrorSuccess ? buffer.ToString() : null);
        });

    /// <summary>
    /// The pure rule over an enumeration of the agent's related products: the
    /// verdict that decides whether the removal may go ahead.
    /// </summary>
    /// <remarks>
    /// <b>Only ERROR_NO_MORE_ITEMS clears a product.</b> Every other non-success
    /// status -- ERROR_BAD_CONFIGURATION, ERROR_INVALID_PARAMETER,
    /// ERROR_NOT_ENOUGH_MEMORY -- means the enumeration did not finish, and
    /// treating "did not finish" as "not the agent" is how an agent uninstalls
    /// itself on the one machine where Windows Installer was unwell. Reaching
    /// the bound without an end marker is the same kind of not-knowing: the
    /// agent's upgrade code has one product, so an enumeration that keeps going
    /// past <see cref="MaxRelatedProducts"/> is not an answer either.
    /// Self-protection fails closed.
    /// </remarks>
    /// <param name="productCode">The product code being asked about.</param>
    /// <param name="enumerate">
    /// One step of the enumeration: the status Windows Installer returned for the
    /// index, and the product code it wrote when that status was success.
    /// </param>
    internal static UpgradeCodeMembership RelatedProductMembership(
        string productCode, Func<uint, (uint Status, string? ProductCode)> enumerate)
    {
        for (uint index = 0; index < MaxRelatedProducts; index++)
        {
            var (status, related) = enumerate(index);

            if (status == ErrorNoMoreItems)
            {
                return UpgradeCodeMembership.NotAgent;
            }

            if (status != ErrorSuccess)
            {
                return UpgradeCodeMembership.Unconfirmed;
            }

            if (string.Equals(related?.Trim(), productCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return UpgradeCodeMembership.Agent;
            }
        }

        return UpgradeCodeMembership.Unconfirmed;
    }

    /// <summary>
    /// A product's registered property from Windows Installer, or null when the
    /// product, the property or the value is absent. Read-only.
    /// </summary>
    internal static string? ReadProductProperty(string productCode, string property)
    {
        uint length = 0;
        var status = Msi.MsiGetProductInfo(productCode, property, null, ref length);
        if ((status != ErrorSuccess && status != ErrorMoreData) || length == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length + 1);
        length += 1;
        if (Msi.MsiGetProductInfo(productCode, property, buffer, ref length) != ErrorSuccess)
        {
            return null;
        }

        var value = buffer.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsProductCode(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return false;
        }

        var trimmed = productCode.Trim();
        return trimmed.Length == 38 && Guid.TryParseExact(trimmed, "B", out _);
    }

    // ---------------------------------------------------------------- packages

    public async ValueTask<ApplicationRemovalOutcome> RemovePackageAsync(
        string packageFullName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var refusal = PackageRefusal(packageFullName);
        if (refusal is not null)
        {
            _logger.LogWarning("Refused to remove package {Package}: {Reason}.", packageFullName, refusal);
            return new ApplicationRemovalOutcome(ApplicationRemovalResult.Refused, null, refusal);
        }

        var fullName = packageFullName.Trim();

        // Belt and braces after the publisher check: whatever the publisher id
        // says, a package whose registered root is under the Windows directory is
        // part of the operating system.
        var registration = FindRegistration(fullName);
        if (registration.Root is not null
            && IsUnderWindowsDirectory(registration.Root, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
        {
            _logger.LogWarning("Refused to remove package {Package}: its root is under the Windows directory.", fullName);
            return new ApplicationRemovalOutcome(ApplicationRemovalResult.Refused, null, RefusedAsSystemComponent);
        }

        // Presence is checked on the machine before the engine is asked, as the
        // product path checks Windows Installer's state. The engine completes a
        // removal of a package nobody has as a success; without this, that
        // would be reported as "removed" for something that was never there.
        if (!registration.Registered && !IsInStateRepositoryIndex(fullName))
        {
            return new ApplicationRemovalOutcome(
                ApplicationRemovalResult.AlreadyRemoved, null, "the package is not registered on this device");
        }

        PackageRemovalReport report;
        try
        {
            report = await WindowsPackageManager.RemoveForAllUsersAsync(fullName, PackageRemovalTimeout, cancellationToken);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "The deployment engine could not be driven to remove {Package}.", fullName);
            return new ApplicationRemovalOutcome(
                ApplicationRemovalResult.Failed, ex.HResult, $"the deployment engine failed (0x{ex.HResult:X8})");
        }

        // The engine's word is checked against the machine before it is
        // repeated, as the product path checks the installer's.
        if (report.Completed && report.Code == 0 && await IsStillRegisteredAsync(fullName, cancellationToken))
        {
            return new ApplicationRemovalOutcome(
                ApplicationRemovalResult.Failed, report.Code,
                "the deployment engine reported success but the package is still registered");
        }

        return MapPackageResult(fullName, report);
    }

    private ApplicationRemovalOutcome MapPackageResult(string fullName, PackageRemovalReport report)
    {
        if (report.Completed && report.Code == 0)
        {
            _logger.LogWarning("Package {Package} removed for all users by an authorized task.", fullName);
            return new(ApplicationRemovalResult.Removed, 0, "removed");
        }

        if (report.Code == WindowsPackageManager.PackageNotFound)
        {
            return new(ApplicationRemovalResult.AlreadyRemoved, report.Code, "the package is not registered on this device");
        }

        var text = report.ErrorText?.Trim().TrimEnd('.');
        if (!string.IsNullOrEmpty(text) && text.Length > MaxErrorTextLength)
        {
            text = text[..MaxErrorTextLength];
        }

        var detail = string.IsNullOrEmpty(text)
            ? $"the deployment engine returned 0x{report.Code:X8}"
            : $"{text} (0x{report.Code:X8})";
        return new(ApplicationRemovalResult.Failed, report.Code, detail);
    }

    /// <summary>
    /// Why a package full name must not reach the deployment engine, or null when
    /// it may. A full name has five underscore-separated segments -- name,
    /// version, architecture, resource id (usually empty), publisher id -- and
    /// the publisher id of Windows' own packages is refused outright.
    /// </summary>
    internal static string? PackageRefusal(string? packageFullName)
    {
        var parts = AppxManifest.FullNameParts(packageFullName);
        if (parts is null
            || parts[1].Length == 0
            || parts[2].Length == 0
            || parts[4].Length != 13
            || !parts[4].All(char.IsAsciiLetterOrDigit)
            || parts.Any(p => p.Any(c => char.IsWhiteSpace(c) || c == '\\' || c == '/' || c == ':')))
        {
            return "refused: the package full name is malformed";
        }

        return string.Equals(parts[4], WindowsInboxPublisherId, StringComparison.OrdinalIgnoreCase)
            ? RefusedAsSystemComponent
            : null;
    }

    /// <summary>Whether a package root is the Windows directory or lies inside it.</summary>
    internal static bool IsUnderWindowsDirectory(string? root, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return false;
        }

        var directory = root.Trim().Trim('"').TrimEnd('\\', '/');
        var windows = windowsDirectory.TrimEnd('\\', '/');
        return string.Equals(directory, windows, StringComparison.OrdinalIgnoreCase)
            || ExecutablePath.IsUnder(directory, windows);
    }

    /// <summary>
    /// The package's registration: whether any readable repository lists the full
    /// name -- machine-wide or in any loaded user hive -- and the root the first
    /// such entry names.
    /// </summary>
    private (bool Registered, string? Root) FindRegistration(string fullName)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var machine = hklm.OpenSubKey(MachineRepositoryPackages + "\\" + fullName);
            if (machine is not null)
            {
                return (true, machine.GetValue("PackageRootFolder") as string);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Could not read the machine package repository for {Package}.", fullName);
        }

        foreach (var (sid, _) in WindowsUserHives.Loaded())
        {
            try
            {
                using var entry = WindowsUserHives.OpenUserSubKey(sid, RepositoryPackages + "\\" + fullName, classes: true);
                if (entry is not null)
                {
                    return (true, entry.GetValue("PackageRootFolder") as string);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Could not read a user package repository for {Package}.", fullName);
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Whether the state repository's index lists the full name. The index covers
    /// every user, signed in or not, which the loaded hives cannot.
    /// </summary>
    private bool IsInStateRepositoryIndex(string fullName)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var entry = hklm.OpenSubKey(StateRepositoryIndex + "\\" + fullName);
            return entry is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Could not read the state repository index for {Package}.", fullName);
            return false;
        }
    }

    /// <summary>Whether the repositories still list the package after the engine said it was gone.</summary>
    private async Task<bool> IsStillRegisteredAsync(string fullName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < PostVerifyAttempts; attempt++)
        {
            if (!FindRegistration(fullName).Registered)
            {
                return false;
            }

            await Task.Delay(PostVerifyInterval, cancellationToken);
        }

        return FindRegistration(fullName).Registered;
    }
}
