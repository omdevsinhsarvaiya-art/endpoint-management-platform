using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Enumerates present PnP devices and their bound drivers through SetupAPI.
/// </summary>
/// <remarks>
/// <para>
/// Read-only: <c>SetupDiGetClassDevs</c> / <c>SetupDiEnumDeviceInfo</c> to walk the
/// present devices, <c>SetupDiGetDeviceProperty</c> for each driver property, and
/// <c>CM_Get_DevNode_Status</c> for the problem code. No process is launched, so
/// <c>pnputil</c> is neither used nor needed (ADR-0005).
/// </para>
/// <para>
/// Unreadable is reported as null throughout, never as a default. A device whose
/// problem code could not be read arrives at the server as unknown, and the server
/// scores unknown separately from healthy -- the same discipline the security
/// posture collector follows for the checks that need elevation.
/// </para>
/// <para>
/// Only devices that are <em>present</em> are enumerated. The alternative includes
/// every device ever attached to the machine, which on a laptop that has seen a few
/// docks and USB sticks is thousands of phantom rows describing hardware that is not
/// there.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDriverCollector(ILogger<WindowsDriverCollector> logger) : IDriverCollector
{
    private readonly ILogger<WindowsDriverCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Cap on devices in one snapshot. Comfortably above a real machine, and it
    /// bounds the work if an enumeration ever misbehaves.
    /// </summary>
    private const int MaxDevices = 4096;

    public ValueTask<IReadOnlyList<InventoryDriver>> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<InventoryDriver>();

        // Signature verification hits the catalogue store and is the slowest thing
        // here, while dozens of devices commonly share one INF. Cached per INF for
        // the life of this snapshot.
        var signatureCache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        var set = UsbNative.SetupDiGetClassDevs(
            IntPtr.Zero, null, IntPtr.Zero, UsbNative.DIGCF_PRESENT | UsbNative.DIGCF_ALLCLASSES);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            _logger.LogError(
                "SetupDiGetClassDevs failed while enumerating drivers (Win32 {Error}).",
                Marshal.GetLastWin32Error());
            return ValueTask.FromResult<IReadOnlyList<InventoryDriver>>([]);
        }

        try
        {
            var info = NewDevInfo();

            for (uint index = 0;
                 results.Count < MaxDevices && UsbNative.SetupDiEnumDeviceInfo(set, index, ref info);
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var instanceId = WindowsUsbDeviceEnumerator.GetStringProperty(
                    set, ref info, UsbNative.DEVPKEY_Device_InstanceId);

                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    info = NewDevInfo();
                    continue;
                }

                var name = WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, UsbNative.DEVPKEY_Device_FriendlyName)
                    ?? WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, UsbNative.DEVPKEY_Device_DeviceDesc)
                    ?? instanceId;

                var infName = WindowsUsbDeviceEnumerator.GetStringProperty(
                    set, ref info, DriverNative.DEVPKEY_Device_DriverInfPath);

                results.Add(new InventoryDriver(
                    InstanceId: instanceId,
                    DeviceName: name,
                    DeviceClass: WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, UsbNative.DEVPKEY_Device_Class),
                    Manufacturer: WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, UsbNative.DEVPKEY_Device_Manufacturer),
                    DriverProvider: WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, DriverNative.DEVPKEY_Device_DriverProvider),
                    DriverVersion: WindowsUsbDeviceEnumerator.GetStringProperty(
                        set, ref info, DriverNative.DEVPKEY_Device_DriverVersion),
                    DriverDate: GetFileTimeProperty(set, ref info, DriverNative.DEVPKEY_Device_DriverDate),
                    InfName: infName,
                    ProblemCode: GetProblemCode(info.DevInst),
                    IsSigned: GetSignatureState(infName, signatureCache)));

                info = NewDevInfo();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad devnode must not cost the whole snapshot: keep what was read.
            _logger.LogWarning(ex, "Driver enumeration stopped early; reporting {Count} device(s).", results.Count);
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }

        return ValueTask.FromResult<IReadOnlyList<InventoryDriver>>(results);
    }

    private static UsbNative.SP_DEVINFO_DATA NewDevInfo() => new()
    {
        CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
    };

    /// <summary>
    /// The device's Windows problem code: 0 when healthy, null when unreadable.
    /// </summary>
    /// <remarks>
    /// Read from <c>CM_Get_DevNode_Status</c> rather than the problem-code device
    /// property, because the status flags say whether a problem number is even
    /// meaningful. Without <c>DN_HAS_PROBLEM</c> the problem number is stale
    /// residue, and reporting it would invent faults on healthy devices.
    /// </remarks>
    internal static int? GetProblemCode(uint devInst)
    {
        if (UsbNative.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != UsbNative.CR_SUCCESS)
        {
            return null;
        }

        if ((status & UsbNative.DN_HAS_PROBLEM) == 0)
        {
            return 0;
        }

        return problem > int.MaxValue ? null : (int)problem;
    }

    private static DateTimeOffset? GetFileTimeProperty(
        IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, UsbNative.DEVPROPKEY key)
    {
        UsbNative.SetupDiGetDeviceProperty(set, ref info, ref key, out _, null, 0, out var required, 0);

        if (required != 8)
        {
            return null;
        }

        var buffer = new byte[8];
        if (!UsbNative.SetupDiGetDeviceProperty(
                set, ref info, ref key, out var type, buffer, required, out _, 0)
            || type != DriverNative.DEVPROP_TYPE_FILETIME)
        {
            return null;
        }

        var ticks = BitConverter.ToInt64(buffer);

        // Zero is "no date recorded" rather than the year 1601.
        if (ticks <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(ticks).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the driver's INF verifies against a trusted catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three-valued on purpose. True means verified; false means Windows told us
    /// specifically that it is unsigned, untrusted or tampered with; null means the
    /// question could not be answered -- no INF recorded, the INF is gone, or the
    /// call failed for a reason that is not a verdict about the signature.
    /// </para>
    /// <para>
    /// Only the last case is common enough to matter, and it is exactly why this is
    /// nullable: reporting "unsigned" because verification could not run would
    /// slander a correctly signed driver.
    /// </para>
    /// </remarks>
    private bool? GetSignatureState(string? infName, Dictionary<string, bool?> cache)
    {
        if (string.IsNullOrWhiteSpace(infName))
        {
            return null;
        }

        if (cache.TryGetValue(infName, out var cached))
        {
            return cached;
        }

        var verdict = VerifyInf(infName);
        cache[infName] = verdict;
        return verdict;
    }

    private bool? VerifyInf(string infName)
    {
        // DriverInfPath is a bare file name ("oem42.inf"); the INF store is the
        // only place SetupVerifyInfFile looks it up from.
        var path = Path.IsPathRooted(infName)
            ? infName
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", infName);

        if (!File.Exists(path))
        {
            return null;
        }

        var signer = new DriverNative.SP_INF_SIGNER_INFO
        {
            cbSize = (uint)Marshal.SizeOf<DriverNative.SP_INF_SIGNER_INFO>(),
            CatalogFile = string.Empty,
            DigitalSigner = string.Empty,
            DigitalSignerVersion = string.Empty,
        };

        try
        {
            if (DriverNative.SetupVerifyInfFile(path, IntPtr.Zero, ref signer))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "SetupVerifyInfFile unavailable; driver signing reported as unknown.");
            return null;
        }

        var error = Marshal.GetLastWin32Error();

        return error switch
        {
            DriverNative.TRUST_E_NOSIGNATURE => false,
            DriverNative.ERROR_NO_CATALOG_FOR_OEM_INF => false,
            DriverNative.TRUST_E_BAD_DIGEST => false,
            DriverNative.CERT_E_UNTRUSTEDROOT => false,

            // Anything else is a failure to answer, not a negative answer.
            _ => null,
        };
    }
}
