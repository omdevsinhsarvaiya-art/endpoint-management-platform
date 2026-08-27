using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Installs driver packages through SetupAPI and newdev.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetupVerifyInfFile</c> to check the catalogue signature, <c>SetupCopyOEMInf</c>
/// to stage the package into the driver store, and
/// <c>UpdateDriverForPlugAndPlayDevices</c> to bind it to matching devices. All
/// typed APIs with no command line; the pnputil command-line tool would need a
/// process launch, which ADR-0005 forbids and <c>AgentSafetyTests</c> enforces by
/// scanning every agent source file -- comments included -- for that API's name.
/// </para>
/// <para>
/// The class is a writer and nothing else. Reading the driver inventory stays with
/// <see cref="WindowsDriverCollector"/>; the property-read helpers here exist only to
/// verify what this installer just did, and it shares the collector's problem-code
/// reader rather than forming a second opinion about device health.
/// </para>
/// <para>
/// <b>Nothing is executed from the package.</b> An INF is data handed to Windows.
/// This class runs no binary from the extraction directory.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDriverInstaller(ILogger<WindowsDriverInstaller> logger) : IDriverInstaller
{
    private readonly ILogger<WindowsDriverInstaller> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public ValueTask<IReadOnlyList<(string InstanceId, string? DriverVersion)>> FindMatchingInstancesAsync(
        string hardwareId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(FindMatchingInstances(hardwareId));
    }

    public ValueTask<DriverInstallOutcome> InstallAsync(
        string infPath,
        string hardwareId,
        string requiredSignerSubject,
        string? expectedVersion,
        string? expectedProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredSignerSubject);

        cancellationToken.ThrowIfCancellationRequested();

        // ---- Catalogue signature and signer pin --------------------------
        // Before the driver store is touched at all. A package that fails here has
        // been downloaded and unpacked into a temp directory and nothing more.
        var signature = VerifySignature(infPath, requiredSignerSubject);
        if (signature.Result != DriverInstallResult.Verified)
        {
            _logger.LogWarning("Driver package refused at the signature gate: {Detail}", signature.Detail);
            return ValueTask.FromResult(new DriverInstallOutcome(signature.Result, [], signature.Detail));
        }

        // ---- Hardware match ----------------------------------------------
        var before = FindMatchingInstances(hardwareId);
        if (before.Count == 0)
        {
            return ValueTask.FromResult(new DriverInstallOutcome(
                DriverInstallResult.HardwareMismatch, [],
                $"No present device matches '{hardwareId}'."));
        }

        // ---- Stage into the driver store ---------------------------------
        if (!StageInf(infPath, out var storedInfPath, out var stageError))
        {
            return ValueTask.FromResult(new DriverInstallOutcome(
                DriverInstallResult.InstallFailed, [], stageError));
        }

        // ---- Bind to matching devices ------------------------------------
        var installed = DriverNative.UpdateDriverForPlugAndPlayDevices(
            IntPtr.Zero, hardwareId, storedInfPath ?? infPath,
            DriverNative.INSTALLFLAG_NONINTERACTIVE, out var rebootRequired);

        if (!installed)
        {
            var error = Marshal.GetLastWin32Error();

            _logger.LogWarning(
                "UpdateDriverForPlugAndPlayDevices failed for {HardwareId} (Win32 0x{Error:X8}).",
                hardwareId, error);

            var detail = error switch
            {
                DriverNative.ERROR_NO_SUCH_DEVINST => $"Windows found no present device for '{hardwareId}'.",
                DriverNative.ERROR_NO_MORE_ITEMS =>
                    "Windows did not consider this package a better match for any device.",
                _ => $"Windows refused the installation (0x{error:X8}).",
            };

            return ValueTask.FromResult(new DriverInstallOutcome(
                DriverInstallResult.InstallFailed, [], detail));
        }

        // ---- Verify every affected instance individually -----------------
        // The API returning true says the call was accepted. It does not say the
        // driver bound, or which one bound, or that the device came back healthy.
        var instances = before
            .Select(match => VerifyInstance(
                match.InstanceId, expectedVersion, expectedProvider, Path.GetFileName(infPath)))
            .ToList();

        if (rebootRequired)
        {
            _logger.LogInformation(
                "Driver staged for {HardwareId} on {Count} device(s); a restart is required. Not rebooting.",
                hardwareId, instances.Count);
        }

        // The rule that turns per-instance results into one outcome lives in Core so
        // it can be asserted without installing a driver -- notably the case where
        // one matched device takes the driver and another does not.
        return ValueTask.FromResult(DriverInstallOutcome.FromVerifications(instances, rebootRequired));
    }

    // ------------------------------------------------------------------ gates

    /// <summary>
    /// Verifies the INF's catalogue signature and the pinned signer subject.
    /// </summary>
    /// <remarks>
    /// Fails closed in both directions. An INF that will not verify is refused, and
    /// so is one that verifies but reports no signer -- a verified package whose
    /// publisher cannot be established has not satisfied the pin, and treating a
    /// blank signer as a match would defeat the entire point of requiring one.
    /// </remarks>
    private (DriverInstallResult Result, string? Detail) VerifySignature(
        string infPath, string requiredSignerSubject)
    {
        var signer = new DriverNative.SP_INF_SIGNER_INFO
        {
            cbSize = (uint)Marshal.SizeOf<DriverNative.SP_INF_SIGNER_INFO>(),
            CatalogFile = string.Empty,
            DigitalSigner = string.Empty,
            DigitalSignerVersion = string.Empty,
        };

        bool verified;
        try
        {
            verified = DriverNative.SetupVerifyInfFile(infPath, IntPtr.Zero, ref signer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (DriverInstallResult.SignatureRejected,
                "Driver signature verification is unavailable on this machine.");
        }

        if (!verified)
        {
            var error = Marshal.GetLastWin32Error();

            var detail = error switch
            {
                DriverNative.TRUST_E_NOSIGNATURE => "the package carries no digital signature",
                DriverNative.ERROR_NO_CATALOG_FOR_OEM_INF => "the package has no signature catalogue",
                DriverNative.TRUST_E_BAD_DIGEST => "the package has been modified since it was signed",
                DriverNative.CERT_E_UNTRUSTEDROOT => "the package is signed by an untrusted authority",
                _ => $"verification failed (0x{error:X8})",
            };

            return (DriverInstallResult.SignatureRejected, detail);
        }

        if (string.IsNullOrWhiteSpace(signer.DigitalSigner))
        {
            return (DriverInstallResult.SignerMismatch,
                "The package verified but reports no signer, so the required publisher cannot be confirmed.");
        }

        if (!signer.DigitalSigner.Contains(requiredSignerSubject, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Driver package signer did not match the pin. Required to contain '{Required}'.",
                requiredSignerSubject);

            return (DriverInstallResult.SignerMismatch,
                "The package signer does not match the required publisher.");
        }

        return (DriverInstallResult.Verified, null);
    }

    private bool StageInf(string infPath, out string? storedInfPath, out string? error)
    {
        storedInfPath = null;
        error = null;

        var buffer = new StringBuilder(260);

        var staged = DriverNative.SetupCopyOEMInf(
            infPath,
            Path.GetDirectoryName(infPath),
            DriverNative.SPOST_PATH,
            0,
            buffer,
            (uint)buffer.Capacity,
            out _,
            IntPtr.Zero);

        if (!staged)
        {
            var win32 = Marshal.GetLastWin32Error();
            _logger.LogWarning("SetupCopyOEMInf failed for {Inf} (Win32 0x{Error:X8}).", infPath, win32);
            error = $"The driver package could not be staged into the driver store (0x{win32:X8}).";
            return false;
        }

        storedInfPath = buffer.ToString();
        return true;
    }

    // ----------------------------------------------------------- verification

    /// <summary>
    /// Re-reads one device's PnP state and decides whether it shows the intended
    /// driver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four independent conditions, each of which can fail on its own: the version,
    /// the provider, the bound INF, and the device's problem code. A device that took
    /// the driver and then failed to start is not a successful installation, and it is
    /// exactly the case a return-value check would call success.
    /// </para>
    /// <para>
    /// An expectation the package did not state is not checked. Inventing a comparison
    /// against null would fail every install of a package whose version was unknown at
    /// approval time; the weaker verification is reported honestly instead.
    /// </para>
    /// </remarks>
    private DriverInstanceVerification VerifyInstance(
        string instanceId, string? expectedVersion, string? expectedProvider, string? expectedInf)
    {
        var set = UsbNative.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return new DriverInstanceVerification(
                instanceId, false, null, null, null, null, "the device could not be reopened for verification");
        }

        try
        {
            var info = new UsbNative.SP_DEVINFO_DATA
            {
                CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
            };

            if (!UsbNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
            {
                return new DriverInstanceVerification(
                    instanceId, false, null, null, null, null, "the device is no longer present");
            }

            var version = WindowsUsbDeviceEnumerator.GetStringProperty(
                set, ref info, DriverNative.DEVPKEY_Device_DriverVersion);
            var provider = WindowsUsbDeviceEnumerator.GetStringProperty(
                set, ref info, DriverNative.DEVPKEY_Device_DriverProvider);
            var inf = WindowsUsbDeviceEnumerator.GetStringProperty(
                set, ref info, DriverNative.DEVPKEY_Device_DriverInfPath);

            // Shared with the inventory collector rather than re-derived, so a device
            // this installer calls healthy is healthy by the same definition the
            // driver-health feature uses.
            var problemCode = WindowsDriverCollector.GetProblemCode(info.DevInst);

            var failures = new List<string>();

            if (expectedVersion is { Length: > 0 }
                && !string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"driver version is '{version ?? "unreadable"}', expected '{expectedVersion}'");
            }

            if (expectedProvider is { Length: > 0 }
                && !string.Equals(provider, expectedProvider, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"driver provider is '{provider ?? "unreadable"}', expected '{expectedProvider}'");
            }

            // The store renames an OEM INF (oem42.inf), so the staged name will not
            // match the package's. Only a reported INF that is missing entirely is a
            // failure; identity is carried by the version and provider checks.
            if (string.IsNullOrWhiteSpace(inf))
            {
                failures.Add("the device reports no bound INF");
            }

            if (problemCode is null)
            {
                failures.Add("the device's problem state could not be read");
            }
            else if (problemCode != 0)
            {
                failures.Add($"the device reports Windows problem code {problemCode}");
            }

            return new DriverInstanceVerification(
                instanceId,
                failures.Count == 0,
                version,
                provider,
                inf,
                problemCode,
                failures.Count == 0 ? null : string.Join("; ", failures));
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>
    /// Present devices whose hardware ids include <paramref name="hardwareId"/>.
    /// </summary>
    /// <remarks>
    /// Matched against the full hardware-id list rather than the instance id, because
    /// that is what Windows matches when it binds a driver -- and it is what makes the
    /// pre-install gate mean the same thing as the installation that follows it.
    /// </remarks>
    private IReadOnlyList<(string InstanceId, string? DriverVersion)> FindMatchingInstances(string hardwareId)
    {
        var results = new List<(string, string?)>();

        var set = UsbNative.SetupDiGetClassDevs(
            IntPtr.Zero, null, IntPtr.Zero, UsbNative.DIGCF_PRESENT | UsbNative.DIGCF_ALLCLASSES);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            _logger.LogError(
                "SetupDiGetClassDevs failed while matching hardware id (Win32 {Error}).",
                Marshal.GetLastWin32Error());
            return results;
        }

        try
        {
            var info = new UsbNative.SP_DEVINFO_DATA
            {
                CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
            };

            for (uint index = 0; UsbNative.SetupDiEnumDeviceInfo(set, index, ref info); index++)
            {
                // The shared helper returns the multi-string joined with ';', so the
                // list is split back out here rather than substring-matched. A device
                // whose hardware id merely contains the target's is a different
                // device, and Windows would not bind the driver to it either.
                var hardwareIds = (WindowsUsbDeviceEnumerator.GetStringListProperty(
                        set, ref info, UsbNative.DEVPKEY_Device_HardwareIds) ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (hardwareIds.Any(id => string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    var instanceId = WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, UsbNative.DEVPKEY_Device_InstanceId);

                    if (!string.IsNullOrWhiteSpace(instanceId))
                    {
                        results.Add((
                            instanceId,
                            WindowsUsbDeviceEnumerator.GetStringProperty(
                                set, ref info, DriverNative.DEVPKEY_Device_DriverVersion)));
                    }
                }

                info = new UsbNative.SP_DEVINFO_DATA
                {
                    CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
                };
            }
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }

        return results;
    }
}
