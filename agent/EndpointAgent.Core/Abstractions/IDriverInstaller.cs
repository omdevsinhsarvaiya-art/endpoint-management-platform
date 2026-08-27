namespace EndpointAgent.Core.Abstractions;

/// <summary>Why a driver package was refused, or how its installation ended.</summary>
public enum DriverInstallResult
{
    /// <summary>Installed and every affected instance verified as expected.</summary>
    Verified = 0,

    /// <summary>
    /// Installed and staged, but the machine must restart before the driver is
    /// active. A successful outcome, and explicitly not the same as Verified.
    /// </summary>
    PendingReboot = 1,

    /// <summary>The INF is unsigned, untrusted, tampered with, or unverifiable.</summary>
    SignatureRejected = 2,

    /// <summary>The catalogue signer did not match the pinned subject.</summary>
    SignerMismatch = 3,

    /// <summary>No present device matches the package's hardware id. Nothing was touched.</summary>
    HardwareMismatch = 4,

    /// <summary>
    /// A matching device already runs a newer driver and the request did not
    /// explicitly authorize a downgrade. Nothing was touched.
    /// </summary>
    DowngradeRefused = 5,

    /// <summary>Windows refused the installation. Nothing became active.</summary>
    InstallFailed = 6,

    /// <summary>
    /// Windows reported success but the endpoint does not show the intended driver.
    /// The most important failure in this enum: an API return value is not evidence.
    /// </summary>
    VerificationFailed = 7,
}

/// <summary>What one affected device instance looks like after installation.</summary>
/// <param name="InstanceId">The PnP instance the installation touched.</param>
/// <param name="Verified">Whether this instance shows the intended driver, active and healthy.</param>
/// <param name="ObservedVersion">Driver version read back, or null when unreadable.</param>
/// <param name="ObservedProvider">Driver provider read back, or null when unreadable.</param>
/// <param name="ObservedInf">Bound INF read back, or null when unreadable.</param>
/// <param name="ProblemCode">PnP problem code after installation. 0 is healthy, null unreadable.</param>
/// <param name="Detail">Why this instance failed verification, when it did.</param>
public sealed record DriverInstanceVerification(
    string InstanceId,
    bool Verified,
    string? ObservedVersion,
    string? ObservedProvider,
    string? ObservedInf,
    int? ProblemCode,
    string? Detail);

/// <param name="Instances">
/// Every present instance the hardware id matched, each verified individually. A
/// hardware id can match more than one device on a machine, and one of them failing
/// while another succeeds is a real outcome that must not be averaged away.
/// </param>
public sealed record DriverInstallOutcome(
    DriverInstallResult Result,
    IReadOnlyList<DriverInstanceVerification> Instances,
    string? Detail)
{
    /// <summary>
    /// Whether the operation is reportable as success. PendingReboot counts: the
    /// package is staged and correct, and the remaining step is a restart this agent
    /// deliberately does not perform.
    /// </summary>
    public bool Succeeded => Result is DriverInstallResult.Verified or DriverInstallResult.PendingReboot;

    /// <summary>
    /// Decides one overall outcome from the per-instance verifications.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted from the Windows installer so the rule can be asserted without
    /// installing a driver. It previously lived inside the P/Invoke path, where the
    /// case that matters most -- one instance succeeding while another fails -- could
    /// only have been exercised by finding a machine with two matching devices and a
    /// package that half-worked on it.
    /// </para>
    /// <para>
    /// <b>Any</b> instance failing makes the whole outcome a failure. A hardware id
    /// can match several devices, and a driver that took on one of them and left
    /// another broken is not a successful installation however the counts read.
    /// </para>
    /// <para>
    /// A pending reboot outranks per-instance verification, because until the machine
    /// restarts the devices legitimately still show the old driver. Reporting that as
    /// a verification failure would turn a correct installation into an error an
    /// operator would retry.
    /// </para>
    /// </remarks>
    public static DriverInstallOutcome FromVerifications(
        IReadOnlyList<DriverInstanceVerification> instances, bool rebootRequired)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (rebootRequired)
        {
            return new DriverInstallOutcome(
                DriverInstallResult.PendingReboot, instances,
                "Installed and staged; a restart is required before the driver becomes active.");
        }

        // Nothing to verify after an installation that was supposed to affect
        // something. Treated as a verification failure rather than a quiet success:
        // an empty result is the absence of evidence, not evidence of success.
        if (instances.Count == 0)
        {
            return new DriverInstallOutcome(
                DriverInstallResult.VerificationFailed, instances,
                "No device could be verified after installation.");
        }

        var failed = instances.Where(i => !i.Verified).ToList();

        if (failed.Count > 0)
        {
            return new DriverInstallOutcome(
                DriverInstallResult.VerificationFailed, instances,
                $"{failed.Count} of {instances.Count} device(s) do not show the expected driver: "
                + string.Join("; ", failed.Select(f => $"{f.InstanceId} ({f.Detail})")));
        }

        return new DriverInstallOutcome(DriverInstallResult.Verified, instances, null);
    }
}

/// <summary>
/// Installs a verified driver package into the Windows driver store and binds it to
/// matching devices.
/// </summary>
/// <remarks>
/// <para>
/// The one place in the agent that changes what kernel code a machine runs, so its
/// contract is narrow: it installs one INF, for one hardware id, only after the
/// caller has hash-verified the archive, and it performs its own catalogue-signature
/// and signer-pin checks as independent gates before Windows sees anything.
/// </para>
/// <para>
/// The implementation must NOT launch a process or a shell (ADR-0005), which rules
/// out <c>pnputil.exe</c>. On Windows it drives SetupAPI and newdev directly.
/// </para>
/// <para>
/// Separate from <see cref="IDriverCollector"/> on purpose. Reading the driver
/// inventory and rewriting it are different privileges, and an interface that only
/// reads must not be extendable into one that writes.
/// </para>
/// </remarks>
public interface IDriverInstaller
{
    /// <summary>
    /// The present PnP instances matching <paramref name="hardwareId"/>, with the
    /// driver version each currently runs. Read-only; backs the hardware-match gate
    /// and the downgrade check before anything is installed.
    /// </summary>
    ValueTask<IReadOnlyList<(string InstanceId, string? DriverVersion)>> FindMatchingInstancesAsync(
        string hardwareId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies and installs the package, then verifies every affected instance.
    /// </summary>
    /// <param name="infPath">Full path to the extracted INF.</param>
    /// <param name="hardwareId">What the package claims to drive.</param>
    /// <param name="requiredSignerSubject">
    /// Substring the catalogue signer's subject must contain. Never null for a driver.
    /// </param>
    /// <param name="expectedVersion">Driver version that must be observable afterwards, when known.</param>
    /// <param name="expectedProvider">Driver provider that must be observable afterwards, when known.</param>
    ValueTask<DriverInstallOutcome> InstallAsync(
        string infPath,
        string hardwareId,
        string requiredSignerSubject,
        string? expectedVersion,
        string? expectedProvider,
        CancellationToken cancellationToken = default);
}
