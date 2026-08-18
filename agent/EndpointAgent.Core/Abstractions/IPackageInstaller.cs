namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Installs a verified MSI package through the operating system's installer
/// service. This is the one place in the agent that changes machine software
/// state, so its contract is deliberately narrow: it installs a single MSI file
/// by path, only after the caller has hash-verified the bytes, and it performs
/// its own Authenticode signer check as a second, independent gate.
/// </summary>
/// <remarks>
/// The implementation must NOT launch a process or a shell (ADR-0005). On Windows
/// it drives the Windows Installer service directly through <c>msi.dll</c>.
/// </remarks>
public interface IPackageInstaller
{
    /// <summary>
    /// Whether the given MSI ProductCode is already installed. Read-only; backs
    /// idempotency (skip an install that is already present) and post-install
    /// verification.
    /// </summary>
    ValueTask<bool> IsProductInstalledAsync(string msiProductCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the MSI at <paramref name="msiPath"/> quietly, with reboots
    /// suppressed. Before installing, verifies the file carries a trusted
    /// Authenticode signature whose signer subject contains
    /// <paramref name="requiredSignerSubject"/> (when non-null); an unsigned or
    /// untrusted file is refused, never installed.
    /// </summary>
    ValueTask<PackageInstallOutcome> InstallAsync(
        string msiPath, string? requiredSignerSubject, CancellationToken cancellationToken = default);
}

public enum PackageInstallResult
{
    Succeeded = 0,

    /// <summary>Installed, but the machine needs a reboot to finish (MSI 3010).</summary>
    SucceededRebootRequired = 1,

    /// <summary>The file was unsigned, untrusted, or the signer did not match the pin. Nothing was installed.</summary>
    SignatureRejected = 2,

    /// <summary>The Windows Installer reported a failure.</summary>
    InstallFailed = 3,
}

/// <param name="Result">Outcome category.</param>
/// <param name="InstallerExitCode">Raw Windows Installer return code, when an install ran.</param>
/// <param name="Detail">Human-readable detail for the task result and logs.</param>
public sealed record PackageInstallOutcome(
    PackageInstallResult Result, uint? InstallerExitCode, string? Detail)
{
    public bool Succeeded => Result is PackageInstallResult.Succeeded or PackageInstallResult.SucceededRebootRequired;
}
