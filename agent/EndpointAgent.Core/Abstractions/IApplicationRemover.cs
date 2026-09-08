namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Removes an installed application through the operating system's own
/// deployment services: a Windows Installer product by product code, or an
/// MSIX/AppX package by full name.
/// </summary>
/// <remarks>
/// <para>
/// This is the second place in the agent that changes machine software state
/// (<see cref="IPackageInstaller"/> is the first) and its contract is as narrow
/// as that one: it names a product or a package that the platform's own
/// inventory recorded, and nothing else. There is no way to hand it a path, a
/// command line or an uninstaller executable. An EXE installer's uninstaller is
/// a program, and the agent launches no program (ADR-0005), so applications
/// registered that way are not removable here and the server never asks.
/// </para>
/// <para>
/// The implementation must NOT launch a process or a shell. On Windows it
/// drives the Windows Installer service through <c>msi.dll</c> and the AppX
/// deployment engine through its COM activation -- typed calls that Windows
/// services carry out. It refuses to remove the agent itself and any
/// operating-system component on its own judgement, whatever the task says:
/// the server applies the same rules first, but this is the side that removes,
/// so this is the side that must be sure.
/// </para>
/// </remarks>
public interface IApplicationRemover
{
    /// <summary>
    /// Removes a per-machine Windows Installer product, quietly, with reboots
    /// suppressed. A product that is not installed is reported as
    /// <see cref="ApplicationRemovalResult.AlreadyRemoved"/>: the desired state
    /// already holds.
    /// </summary>
    ValueTask<ApplicationRemovalOutcome> RemoveWindowsInstallerProductAsync(
        string productCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an MSIX/AppX package for every user of the device. A package that
    /// is not registered is reported as
    /// <see cref="ApplicationRemovalResult.AlreadyRemoved"/>.
    /// </summary>
    ValueTask<ApplicationRemovalOutcome> RemovePackageAsync(
        string packageFullName, CancellationToken cancellationToken = default);
}

public enum ApplicationRemovalResult
{
    Removed = 0,

    /// <summary>Removed, but the machine needs a restart to finish (MSI 3010 / 1641).</summary>
    RemovedRebootRequired = 1,

    /// <summary>Nothing by that identity is installed; the desired state already holds.</summary>
    AlreadyRemoved = 2,

    /// <summary>
    /// The remover declined: the identity was malformed, or names the agent or an
    /// operating-system component. Nothing was touched.
    /// </summary>
    Refused = 3,

    /// <summary>The deployment service reported a failure.</summary>
    Failed = 4,
}

/// <param name="Result">Outcome category.</param>
/// <param name="Code">
/// The raw Windows Installer return code, or the deployment engine's HRESULT,
/// when one was produced.
/// </param>
/// <param name="Detail">
/// Human-readable detail for the task result and logs. Never a path or a command
/// line.
/// </param>
public sealed record ApplicationRemovalOutcome(
    ApplicationRemovalResult Result, long? Code, string? Detail)
{
    public bool Succeeded => Result is ApplicationRemovalResult.Removed
        or ApplicationRemovalResult.RemovedRebootRequired
        or ApplicationRemovalResult.AlreadyRemoved;
}
