using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Applies USB storage access state on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms, both per-device and both documented Windows API:
/// </para>
/// <list type="bullet">
///   <item><b>Restricted</b> — the device instance is disabled through SetupAPI
///   (<c>DIF_PROPERTYCHANGE</c> / <c>DICS_DISABLE</c>), exactly what Device
///   Manager's Disable does. No volume is created, so there is no drive letter
///   and nothing to race against.</item>
///   <item><b>Read-only</b> — the device is enabled and each physical disk
///   beneath it is marked read-only with
///   <c>IOCTL_DISK_SET_DISK_ATTRIBUTES</c>. Windows itself then refuses writes,
///   creates, renames and deletes; this is not an ACL that an administrator on
///   the endpoint can edit.</item>
/// </list>
/// <para>
/// A class-wide alternative exists — the <c>StorageDevicePolicies\WriteProtect</c>
/// and removable-storage Group Policy values — and was deliberately not used.
/// Both are all-or-nothing for every removable device on the machine, so they
/// cannot express "this one approved stick, read-only, for the next two hours",
/// which is the entire requirement.
/// </para>
/// <para>
/// <b>Limits, stated plainly.</b> The read-only attribute is not persisted to
/// the device (<c>Persist = false</c>), so it governs this machine only and does
/// not follow the stick elsewhere — that is deliberate, since altering someone's
/// hardware is not this platform's business. And a user holding local
/// administrator rights on the endpoint can stop the agent service or re-enable
/// the device by hand; a user-mode agent cannot prevent that, and this code does
/// not pretend otherwise. What it does guarantee is that such tampering is
/// visible: the next report shows the device as Drifted rather than Enforced.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbPolicyEnforcer(ILogger<WindowsUsbPolicyEnforcer> logger) : IUsbPolicyEnforcer
{
    /// <summary>How long to wait for a disk to appear after enabling a device.</summary>
    private static readonly TimeSpan DiskArrivalTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DiskPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<WindowsUsbPolicyEnforcer> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public UsbEnforcementResult Restrict(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        try
        {
            if (!SetDeviceEnabled(instanceId, enabled: false, out var error))
            {
                return UsbEnforcementResult.Failed(error);
            }

            _logger.LogInformation("USB storage device {InstanceId} is restricted (disabled).", instanceId);
            return UsbEnforcementResult.Ok;
        }
        catch (Exception ex)
        {
            return UsbEnforcementResult.Failed($"Restrict failed: {ex.Message}");
        }
    }

    public UsbEnforcementResult AllowReadOnly(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        try
        {
            if (!SetDeviceEnabled(instanceId, enabled: true, out var error))
            {
                return UsbEnforcementResult.Failed(error);
            }

            // Enabling is asynchronous: the disk devnode and its interface appear
            // a moment later. Marking read-only before the disk exists would
            // silently do nothing, which is the worst possible outcome here.
            var disks = WaitForDisks(instanceId);

            if (disks.Count == 0)
            {
                return UsbEnforcementResult.Failed(
                    "The device was enabled but no disk appeared within "
                    + $"{DiskArrivalTimeout.TotalSeconds:0} seconds, so read-only could not be applied. "
                    + "The device has been left restricted.");
            }

            foreach (var diskPath in disks)
            {
                if (!SetDiskReadOnly(diskPath, readOnly: true, out var diskError))
                {
                    // Could not make it read-only, so it must not stay writable.
                    // Falling back to Restricted is the only safe outcome: the
                    // alternative is an accessible, writable removable disk that
                    // the console believes is read-only.
                    _logger.LogError(
                        "Could not mark {DiskPath} read-only ({Error}); restricting the device instead.",
                        diskPath, diskError);

                    SetDeviceEnabled(instanceId, enabled: false, out _);

                    return UsbEnforcementResult.Failed(
                        $"Read-only could not be applied ({diskError}); the device was restricted instead.");
                }
            }

            _logger.LogInformation(
                "USB storage device {InstanceId} is read-only across {Count} disk(s).",
                instanceId, disks.Count);

            return UsbEnforcementResult.Ok;
        }
        catch (Exception ex)
        {
            // Any unexpected failure ends with the device restricted, never open.
            SetDeviceEnabled(instanceId, enabled: false, out _);
            return UsbEnforcementResult.Failed($"Read-only enforcement failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns a device to the state it would be in with no agent installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both mechanisms have to be undone, and the order matters. The read-only
    /// attribute can only be cleared on a disk that exists, and the disk only
    /// exists once the devnode is enabled — so enable first, wait for the disk,
    /// then clear the bit.
    /// </para>
    /// <para>
    /// Unlike <see cref="AllowReadOnly"/>, a failure here does <b>not</b> fall
    /// back to restricting the device. The whole point of this method is that the
    /// product is standing down; re-disabling a device because the release went
    /// wrong would be the exact behaviour it exists to prevent. A failure is
    /// reported so the caller keeps the device on its release list and tries
    /// again.
    /// </para>
    /// </remarks>
    public UsbEnforcementResult Release(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        try
        {
            if (!SetDeviceEnabled(instanceId, enabled: true, out var error))
            {
                return UsbEnforcementResult.Failed(error);
            }

            // An absent device enables to nothing and has no disks to clear. That
            // is a complete release: the registry flag is what kept it disabled,
            // and SetDeviceEnabled has already cleared it.
            var failures = new List<string>();

            foreach (var diskPath in WaitForDisks(instanceId))
            {
                if (!SetDiskWritable(diskPath, out var diskError))
                {
                    failures.Add($"{diskPath}: {diskError}");
                }
            }

            if (failures.Count > 0)
            {
                return UsbEnforcementResult.Failed(
                    "The device was re-enabled but the read-only attribute could not be cleared on "
                    + $"{failures.Count} disk(s): {string.Join("; ", failures)}. Unplugging and reattaching "
                    + "the device clears it, as the attribute was never persisted to the hardware.");
            }

            _logger.LogInformation(
                "USB storage device {InstanceId} released; it now behaves as on an unmanaged machine.",
                instanceId);

            return UsbEnforcementResult.Ok;
        }
        catch (Exception ex)
        {
            return UsbEnforcementResult.Failed($"Release failed: {ex.Message}");
        }
    }

    /// <summary>Enables or disables a device instance through SetupAPI.</summary>
    private bool SetDeviceEnabled(string instanceId, bool enabled, out string error)
    {
        error = "";

        var set = UsbNative.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            error = $"SetupDiCreateDeviceInfoList failed (Win32 {Marshal.GetLastWin32Error()}).";
            return false;
        }

        try
        {
            var info = new UsbNative.SP_DEVINFO_DATA
            {
                CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
            };

            if (!UsbNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
            {
                // A device that is no longer attached cannot be in a wrong state,
                // so an absent device is a no-op success rather than an error:
                // reporting failure for an unplugged stick would fill the console
                // with alarms about devices that pose no risk.
                var win32 = Marshal.GetLastWin32Error();
                if (win32 == UsbNative.ERROR_NO_SUCH_DEVINST || win32 == UsbNative.ERROR_FILE_NOT_FOUND)
                {
                    _logger.LogDebug("USB device {InstanceId} is not present; nothing to change.", instanceId);
                    return true;
                }

                error = $"SetupDiOpenDeviceInfo failed for {instanceId} (Win32 {win32}).";
                return false;
            }

            var parameters = new UsbNative.SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new UsbNative.SP_CLASSINSTALL_HEADER
                {
                    CbSize = (uint)Marshal.SizeOf<UsbNative.SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = UsbNative.DIF_PROPERTYCHANGE,
                },
                StateChange = enabled ? UsbNative.DICS_ENABLE : UsbNative.DICS_DISABLE,
                Scope = UsbNative.DICS_FLAG_CONFIGSPECIFIC,
                HwProfile = 0,
            };

            if (!UsbNative.SetupDiSetClassInstallParams(
                    set, ref info, ref parameters, (uint)Marshal.SizeOf<UsbNative.SP_PROPCHANGE_PARAMS>()))
            {
                error = $"SetupDiSetClassInstallParams failed (Win32 {Marshal.GetLastWin32Error()}).";
                return false;
            }

            if (!UsbNative.SetupDiCallClassInstaller(UsbNative.DIF_PROPERTYCHANGE, set, ref info))
            {
                var win32 = Marshal.GetLastWin32Error();
                error = win32 == UsbNative.ERROR_ACCESS_DENIED
                    ? "Access denied changing the device state. The agent must run as LocalSystem."
                    : $"SetupDiCallClassInstaller failed (Win32 {win32}: {new Win32Exception(win32).Message}).";
                return false;
            }

            return true;
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Polls for the disk interfaces beneath a USB device after enabling it.</summary>
    /// <remarks>
    /// The wall clock is deliberate here, and the one place in this feature that
    /// uses it. Every expiry decision goes through an injected
    /// <see cref="TimeProvider"/> so it can be driven in tests — but this is not
    /// a policy decision, it is a bounded wait for real hardware to enumerate.
    /// A virtual clock would either spin forever or time out instantly.
    /// </remarks>
    private List<string> WaitForDisks(string instanceId)
    {
        var deadline = DateTime.UtcNow + DiskArrivalTimeout;

        while (true)
        {
            var disks = FindDiskInterfaces(instanceId);
            if (disks.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return disks;
            }

            Thread.Sleep(DiskPollInterval);
        }
    }

    /// <summary>
    /// Finds the <c>\\?\</c> disk interface paths belonging to one USB device.
    /// </summary>
    /// <remarks>
    /// Walks the devnode subtree rather than scanning <c>\\.\PHYSICALDRIVEn</c>,
    /// because scanning would require matching disks back to their USB parent by
    /// guesswork. Descending from the instance ID we were asked about means the
    /// disks we touch provably belong to that device and no other — nobody's
    /// internal drive can be caught by a mismatch.
    /// </remarks>
    private List<string> FindDiskInterfaces(string instanceId)
    {
        var paths = new List<string>();

        if (UsbNative.CM_Locate_DevNode(out var root, instanceId, 0) != UsbNative.CR_SUCCESS)
        {
            return paths;
        }

        var pending = new Stack<uint>();
        pending.Push(root);
        var visited = 0;

        while (pending.Count > 0 && visited++ < 256)
        {
            var current = pending.Pop();

            if (UsbNative.CM_Get_Child(out var child, current, 0) == UsbNative.CR_SUCCESS)
            {
                pending.Push(child);

                var sibling = child;
                while (UsbNative.CM_Get_Sibling(out var next, sibling, 0) == UsbNative.CR_SUCCESS)
                {
                    pending.Push(next);
                    sibling = next;
                }
            }

            if (WindowsUsbDeviceEnumerator.GetDeviceId(current) is not { Length: > 0 } deviceId)
            {
                continue;
            }

            paths.AddRange(GetDiskInterfaces(deviceId));
        }

        return paths;
    }

    private static List<string> GetDiskInterfaces(string deviceId)
    {
        var result = new List<string>();

        if (UsbNative.CM_Get_Device_Interface_List_Size(
                out var length, ref UsbNative.GuidDevInterfaceDisk, deviceId,
                UsbNative.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != UsbNative.CR_SUCCESS
            || length <= 1)
        {
            return result;
        }

        var buffer = new char[length];
        if (UsbNative.CM_Get_Device_Interface_List(
                ref UsbNative.GuidDevInterfaceDisk, deviceId, buffer, length,
                UsbNative.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != UsbNative.CR_SUCCESS)
        {
            return result;
        }

        result.AddRange(
            new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return result;
    }

    /// <summary>Clears the read-only attribute this agent set on a disk.</summary>
    private bool SetDiskWritable(string diskPath, out string error) =>
        SetDiskReadOnly(diskPath, readOnly: false, out error);

    /// <summary>Sets or clears the read-only attribute on one physical disk.</summary>
    /// <param name="readOnly">
    /// True to mark the disk read-only, false to clear the bit. The mask is
    /// limited to the read-only bit either way, so nothing else Windows tracks on
    /// the disk — the OFFLINE bit in particular — is disturbed.
    /// </param>
    private bool SetDiskReadOnly(string diskPath, bool readOnly, out string error)
    {
        error = "";

        using var handle = UsbNative.CreateFile(
            diskPath,
            UsbNative.GENERIC_READ | UsbNative.GENERIC_WRITE,
            UsbNative.FILE_SHARE_READ | UsbNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            UsbNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = $"could not open {diskPath} (Win32 {Marshal.GetLastWin32Error()})";
            return false;
        }

        var attributes = new UsbNative.SET_DISK_ATTRIBUTES
        {
            Version = (uint)Marshal.SizeOf<UsbNative.SET_DISK_ATTRIBUTES>(),

            // Not persisted to the device. This control governs this endpoint;
            // writing a flag into someone's hardware that follows it to every
            // other machine is not ours to do.
            Persist = 0,

            Attributes = readOnly ? UsbNative.DISK_ATTRIBUTE_READ_ONLY : 0,

            // Mask limited to the read-only bit, so the OFFLINE bit and anything
            // else Windows is tracking is left exactly as it was.
            AttributesMask = UsbNative.DISK_ATTRIBUTE_READ_ONLY,
        };

        var size = Marshal.SizeOf<UsbNative.SET_DISK_ATTRIBUTES>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(attributes, buffer, fDeleteOld: false);

            if (!UsbNative.DeviceIoControl(
                    handle, UsbNative.IOCTL_DISK_SET_DISK_ATTRIBUTES,
                    buffer, (uint)size, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                error = $"IOCTL_DISK_SET_DISK_ATTRIBUTES failed (Win32 {Marshal.GetLastWin32Error()})";
                return false;
            }

            // Make the volume manager re-read the disk. Without this the
            // attribute is set but an already-mounted volume keeps its
            // read-write mount, which would leave the control claimed but not
            // in force for the drive letter the user is actually looking at.
            UsbNative.DeviceIoControl(
                handle, UsbNative.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            return VerifyReadOnly(handle, readOnly, out error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the attribute back and confirms the read-only bit is actually set.
    /// </summary>
    /// <remarks>
    /// The IOCTL returning success is not the same as the disk being read-only,
    /// and the difference matters enough to spend one more call on. Everything
    /// upstream — the task result, the report, the badge in the console — is
    /// derived from this answer, so it is a measurement rather than an
    /// assumption.
    /// </remarks>
    private static bool VerifyReadOnly(SafeFileHandle handle, bool expected, out string error)
    {
        error = "";

        var size = Marshal.SizeOf<UsbNative.GET_DISK_ATTRIBUTES>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (!UsbNative.DeviceIoControl(
                    handle, UsbNative.IOCTL_DISK_GET_DISK_ATTRIBUTES,
                    IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
            {
                error = $"could not read disk attributes back (Win32 {Marshal.GetLastWin32Error()})";
                return false;
            }

            var current = Marshal.PtrToStructure<UsbNative.GET_DISK_ATTRIBUTES>(buffer);

            var isReadOnly = (current.Attributes & UsbNative.DISK_ATTRIBUTE_READ_ONLY) != 0;

            if (isReadOnly != expected)
            {
                error = expected
                    ? "the disk did not report the read-only attribute after it was set"
                    : "the disk still reports the read-only attribute after it was cleared";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
