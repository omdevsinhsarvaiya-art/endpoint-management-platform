using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Enumerates USB devices with SetupAPI and classifies them.
/// </summary>
/// <remarks>
/// <para>
/// Enumerates the <c>USB</c> device tree — the physical devices, not their
/// function interfaces — so a stick appears once, keyed by the instance ID that
/// policy is written against.
/// </para>
/// <para>
/// Classification reads the driver service and the class of the device and of
/// its descendants, never the friendly name. Names are chosen by the device
/// itself, so classifying on them would let a stick that calls itself
/// "USB Keyboard" avoid storage policy entirely.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbDeviceEnumerator(ILogger<WindowsUsbDeviceEnumerator> logger)
    : IUsbDeviceEnumerator
{
    /// <summary>
    /// Services that mean "this is removable mass storage".
    /// </summary>
    /// <remarks>
    /// <c>USBSTOR</c> is the classic bulk-only mass storage driver;
    /// <c>UASPStor</c> is USB Attached SCSI, which faster drives bind to
    /// instead. Missing the second one would leave a whole category of USB
    /// disks unrestricted, which is exactly the sort of gap that makes a
    /// control worthless.
    /// </remarks>
    private static readonly HashSet<string> StorageServices =
        new(StringComparer.OrdinalIgnoreCase) { "USBSTOR", "UASPStor", "uaspstor", "usbstor" };

    private readonly ILogger<WindowsUsbDeviceEnumerator> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public IReadOnlyList<UsbDeviceInfo> Enumerate()
    {
        var results = new List<UsbDeviceInfo>();

        var set = UsbNative.SetupDiGetClassDevs(
            IntPtr.Zero, "USB", IntPtr.Zero, UsbNative.DIGCF_PRESENT | UsbNative.DIGCF_ALLCLASSES);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            _logger.LogError(
                "SetupDiGetClassDevs failed for the USB enumerator (Win32 {Error}).",
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
                var instanceId = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_InstanceId);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                var service = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Service);
                var deviceClass = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Class);
                var manufacturer = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Manufacturer);
                var friendlyName = GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_FriendlyName)
                    ?? GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_DeviceDesc);
                var hardwareIds = GetStringListProperty(set, ref info, UsbNative.DEVPKEY_Device_HardwareIds);
                var compatibleIds = GetStringListProperty(set, ref info, UsbNative.DEVPKEY_Device_CompatibleIds);

                var (vendorId, productId, serial) = ParseInstanceId(instanceId);

                results.Add(new UsbDeviceInfo(
                    instanceId,
                    Classify(instanceId, service, deviceClass, compatibleIds),
                    vendorId,
                    productId,
                    serial,
                    manufacturer,
                    friendlyName,
                    hardwareIds,
                    IsEnabled(instanceId)));

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

    /// <summary>
    /// Splits <c>USB\VID_0781&amp;PID_5581\ABC123</c> into its parts.
    /// </summary>
    /// <remarks>
    /// The third segment is only treated as a serial when it is genuinely one.
    /// Windows synthesises an instance segment for devices that expose no
    /// serial — <c>7&amp;2f3c1b2&amp;0&amp;2</c> and similar — which encodes the
    /// port path, not the device. Reporting that as a serial would produce a
    /// grant that follows the USB port: unplug the approved stick, plug in a
    /// different one, and it would inherit the access. The ampersand is the
    /// reliable tell, so a segment containing one yields null.
    /// </remarks>
    internal static (string? VendorId, string? ProductId, string? Serial) ParseInstanceId(string instanceId)
    {
        var parts = instanceId.Split('\\');
        string? vendorId = null;
        string? productId = null;
        string? serial = null;

        if (parts.Length >= 2)
        {
            foreach (var token in parts[1].Split('&'))
            {
                if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                {
                    vendorId = token[4..];
                }
                else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                {
                    productId = token[4..];
                }
            }
        }

        if (parts.Length >= 3 && parts[2].Length > 0 && !parts[2].Contains('&', StringComparison.Ordinal))
        {
            serial = parts[2];
        }

        return (vendorId, productId, serial);
    }

    /// <summary>
    /// Decides what a device is, from its driver and the drivers of everything
    /// beneath it.
    /// </summary>
    /// <remarks>
    /// The descendant walk matters for composite devices: a USB stick with a
    /// card reader, or a headset that also presents a HID interface, hangs its
    /// real function drivers off child devnodes, and the parent's own class is
    /// just "USB". Storage is checked first and wins over everything else — a
    /// composite device that contains storage <em>is</em> storage as far as this
    /// control is concerned.
    /// </remarks>
    internal static UsbClass Classify(
        string instanceId, string? service, string? deviceClass, string? compatibleIds = null)
    {
        // A hub is a hub, decided before anything else and never from what is
        // plugged into it. This ordering is not cosmetic: it is the guard that
        // stops a hub inheriting the class of its children.
        if (IsHub(instanceId, service))
        {
            return UsbClass.Hub;
        }

        // Works while the device is disabled, which the descendant walk cannot.
        // A restricted stick has no driver and no child devnodes, so this is the
        // only signal left that says "removable storage".
        if (DeclaresMassStorage(compatibleIds))
        {
            return UsbClass.Storage;
        }

        var services = new List<string>();
        var classes = new List<string>();

        if (!string.IsNullOrWhiteSpace(service))
        {
            services.Add(service);
        }

        if (!string.IsNullOrWhiteSpace(deviceClass))
        {
            classes.Add(deviceClass);
        }

        CollectDescendantTraits(instanceId, services, classes);

        if (services.Any(StorageServices.Contains)
            || classes.Any(c => c.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)
                || c.Equals("USBDevice", StringComparison.OrdinalIgnoreCase) && services.Any(StorageServices.Contains)))
        {
            return UsbClass.Storage;
        }

        if (classes.Any(c => c.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.Keyboard;
        }

        if (classes.Any(c => c.Equals("Mouse", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.Mouse;
        }

        if (classes.Any(c => c.Equals("Net", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbClass.NetworkAdapter;
        }

        return classes.Count > 0 || services.Count > 0 ? UsbClass.Other : UsbClass.Unknown;
    }

    /// <summary>
    /// True for a hub, from the device's own identity only.
    /// </summary>
    /// <remarks>
    /// Checked before the storage rules and never from descendants, because the
    /// descendant walk reaches every device plugged into a hub. Without this
    /// ordering a hub with a USB stick attached collects <c>USBSTOR</c> from that
    /// stick and classifies as storage — and the agent then restricts the
    /// <em>hub</em>, taking every device on it down with it.
    /// </remarks>
    internal static bool IsHub(string instanceId, string? service) =>
        instanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
        || (service is { Length: > 0 } && service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the device itself advertises the USB mass-storage interface
    /// class (08) in its compatible IDs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compatible IDs are written by the bus driver from the device's own
    /// descriptors and stay in the registry whether or not the device is
    /// started. That is what makes this the right signal for a device the agent
    /// has restricted: the driver service is gone and the child devnodes are
    /// gone, but <c>USB\Class_08&amp;SubClass_06&amp;Prot_50</c> remains.
    /// </para>
    /// <para>
    /// A composite device whose storage function sits behind an interface child
    /// advertises <c>USB\COMPOSITE</c> here instead, so this does not catch
    /// every case on its own; the service and descendant rules below still cover
    /// those while the device is enabled.
    /// </para>
    /// </remarks>
    internal static bool DeclaresMassStorage(string? compatibleIds) =>
        compatibleIds is { Length: > 0 }
        && compatibleIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsMassStorageCompatibleId);

    /// <summary>
    /// One compatible ID, matched on the whole class token.
    /// </summary>
    /// <remarks>
    /// A plain prefix test would also accept <c>USB\Class_080</c>. USB class
    /// codes are two hex digits, so that is not a real device — but a
    /// classification rule that can be widened by appending a character is the
    /// wrong shape for something that decides whether a control applies.
    /// </remarks>
    private static bool IsMassStorageCompatibleId(string id) =>
        id.Equals(@"USB\Class_08", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(@"USB\Class_08&", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the descendant walk may descend from one node into another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk exists to find the function drivers of <em>one physical
    /// device</em> — the <c>USBSTOR</c> node under a stick, the HID node under a
    /// keyboard. It must never cross into a different device, and the place that
    /// happens is a hub, whose children are every other device on the bus.
    /// </para>
    /// <para>
    /// The rule: a child not enumerated by <c>USB</c> belongs to this device
    /// (<c>USBSTOR\...</c>, <c>SCSI\...</c>, <c>HID\...</c>). A child that
    /// <em>is</em> <c>USB</c>-enumerated is another device on the bus — unless it
    /// is an interface of this same composite device, which Windows names with
    /// the same VID and PID plus an <c>&amp;MI_</c> segment.
    /// </para>
    /// </remarks>
    internal static bool MayDescendInto(string parentInstanceId, string childInstanceId)
    {
        if (!childInstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!childInstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var (parentVid, parentPid, _) = ParseInstanceId(parentInstanceId);
        var (childVid, childPid, _) = ParseInstanceId(childInstanceId);

        return parentVid is not null
            && string.Equals(parentVid, childVid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parentPid, childPid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Walks the devnode subtree, gathering services and classes.</summary>
    private static void CollectDescendantTraits(
        string instanceId, List<string> services, List<string> classes)
    {
        if (UsbNative.CM_Locate_DevNode(out var root, instanceId, 0) != UsbNative.CR_SUCCESS)
        {
            return;
        }

        var rootId = instanceId;

        // Iterative, with a hard node cap. A cycle in the devnode tree should be
        // impossible, but "should be impossible" is a poor reason to let a
        // service thread spin forever inside a driver-supplied structure.
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

            if (current == root)
            {
                continue;
            }

            var childId = GetDeviceId(current);
            if (childId is null)
            {
                continue;
            }

            // The boundary between "part of this device" and "a different device
            // that happens to hang off it". Crossing it is what made a hub look
            // like storage.
            if (!MayDescendInto(rootId, childId))
            {
                continue;
            }

            ReadNodeTraits(childId, services, classes);
        }
    }

    private static void ReadNodeTraits(string instanceId, List<string> services, List<string> classes)
    {
        var set = UsbNative.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return;
        }

        try
        {
            var info = new UsbNative.SP_DEVINFO_DATA
            {
                CbSize = (uint)Marshal.SizeOf<UsbNative.SP_DEVINFO_DATA>(),
            };

            if (!UsbNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info))
            {
                return;
            }

            if (GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Service) is { Length: > 0 } service)
            {
                services.Add(service);
            }

            if (GetStringProperty(set, ref info, UsbNative.DEVPKEY_Device_Class) is { Length: > 0 } deviceClass)
            {
                classes.Add(deviceClass);
            }
        }
        finally
        {
            UsbNative.SetupDiDestroyDeviceInfoList(set);
        }
    }

    internal static string? GetDeviceId(uint devInst)
    {
        if (UsbNative.CM_Get_Device_ID_Size(out var length, devInst, 0) != UsbNative.CR_SUCCESS || length == 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        if (UsbNative.CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) != UsbNative.CR_SUCCESS)
        {
            return null;
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    /// <summary>True when Windows has the device started rather than disabled.</summary>
    private static bool IsEnabled(string instanceId)
    {
        if (UsbNative.CM_Locate_DevNode(out var devInst, instanceId, 0) != UsbNative.CR_SUCCESS)
        {
            return false;
        }

        if (UsbNative.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != UsbNative.CR_SUCCESS)
        {
            return false;
        }

        return (status & UsbNative.DN_HAS_PROBLEM) == 0 || problem != UsbNative.CM_PROB_DISABLED;
    }

    internal static string? GetStringProperty(
        IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, UsbNative.DEVPROPKEY key)
    {
        UsbNative.SetupDiGetDeviceProperty(
            set, ref info, ref key, out _, null, 0, out var required, 0);

        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!UsbNative.SetupDiGetDeviceProperty(
                set, ref info, ref key, out var type, buffer, required, out _, 0))
        {
            return null;
        }

        if (type != UsbNative.DEVPROP_TYPE_STRING)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0') is { Length: > 0 } value ? value : null;
    }

    /// <summary>Reads a REG_MULTI_SZ-style property and joins it with semicolons.</summary>
    internal static string? GetStringListProperty(
        IntPtr set, ref UsbNative.SP_DEVINFO_DATA info, UsbNative.DEVPROPKEY key)
    {
        UsbNative.SetupDiGetDeviceProperty(
            set, ref info, ref key, out _, null, 0, out var required, 0);

        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!UsbNative.SetupDiGetDeviceProperty(
                set, ref info, ref key, out var type, buffer, required, out _, 0))
        {
            return null;
        }

        if (type is not (UsbNative.DEVPROP_TYPE_STRING_LIST or UsbNative.DEVPROP_TYPE_STRING))
        {
            return null;
        }

        var entries = Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return entries.Length == 0 ? null : string.Join(';', entries);
    }
}
