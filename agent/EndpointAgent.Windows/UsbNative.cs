using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows;

/// <summary>
/// Win32 interop for USB device enumeration and storage access control.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is documented public API: SetupAPI and CfgMgr32 for device
/// enumeration and enable/disable, and the disk IOCTL
/// <c>IOCTL_DISK_SET_DISK_ATTRIBUTES</c> for read-only. No shell, no PowerShell,
/// no kernel driver, no undocumented behaviour (ADR-0005).
/// </para>
/// <para>
/// Kept in one file so the privileged surface the agent uses for USB control is
/// small enough to read in a sitting and review as a unit. Declared with
/// <c>DllImport</c> to match the rest of the agent's interop; the source
/// -generated alternative would require enabling unsafe code across the whole
/// assembly, which is a poor trade for a handful of calls.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class UsbNative
{
    private const string SetupApi = "setupapi.dll";
    private const string CfgMgr = "cfgmgr32.dll";
    private const string Kernel32 = "kernel32.dll";

    // ---- SetupAPI: device info sets ---------------------------------------

    internal const uint DIGCF_PRESENT = 0x00000002;
    internal const uint DIGCF_ALLCLASSES = 0x00000004;

    /// <summary>DIF_PROPERTYCHANGE — the install function that enables/disables.</summary>
    internal const uint DIF_PROPERTYCHANGE = 0x00000012;

    internal const uint DICS_ENABLE = 0x00000001;
    internal const uint DICS_DISABLE = 0x00000002;

    /// <summary>
    /// Change this hardware profile only. The global scope would write a disable
    /// flag that persists across profiles; config-specific is what Device
    /// Manager itself uses for an enable/disable.
    /// </summary>
    internal const uint DICS_FLAG_CONFIGSPECIFIC = 0x00000002;

    /// <summary>ERROR_NO_SUCH_DEVINST — the instance is not present on this machine.</summary>
    internal const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_CLASSINSTALL_HEADER
    {
        public uint CbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid Fmtid;
        public uint Pid;
    }

    // DEVPKEY_Device_* from devpkey.h. The first GUID is the format id shared by
    // the common device properties; the second is DEVPKEY_Device_InstanceId's.
    private static readonly Guid DevPropClassCommon = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DevPropClassDevice = new("78c34fc8-104a-4aca-9ea4-524d52996e57");

    internal static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new() { Fmtid = DevPropClassCommon, Pid = 2 };
    internal static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds = new() { Fmtid = DevPropClassCommon, Pid = 3 };
    internal static readonly DEVPROPKEY DEVPKEY_Device_Service = new() { Fmtid = DevPropClassCommon, Pid = 6 };
    internal static readonly DEVPROPKEY DEVPKEY_Device_Class = new() { Fmtid = DevPropClassCommon, Pid = 9 };
    internal static readonly DEVPROPKEY DEVPKEY_Device_Manufacturer = new() { Fmtid = DevPropClassCommon, Pid = 13 };
    internal static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new() { Fmtid = DevPropClassCommon, Pid = 14 };
    internal static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new() { Fmtid = DevPropClassDevice, Pid = 256 };

    internal const uint DEVPROP_TYPE_STRING = 0x00000012;
    internal const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;

    [DllImport(SetupApi, EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport(SetupApi, SetLastError = true)]
    internal static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport(SetupApi, EntryPoint = "SetupDiOpenDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiOpenDeviceInfo(
        IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent, uint flags,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(SetupApi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(SetupApi, EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport(SetupApi, EntryPoint = "SetupDiSetClassInstallParamsW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        uint classInstallParamsSize);

    [DllImport(SetupApi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiCallClassInstaller(
        uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(SetupApi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---- CfgMgr32: devnode tree and interfaces -----------------------------

    internal const int CR_SUCCESS = 0;

    /// <summary>DN_HAS_PROBLEM — the devnode is reporting a problem code.</summary>
    internal const uint DN_HAS_PROBLEM = 0x00000400;

    /// <summary>CM_PROB_DISABLED — that problem is "disabled by the user/admin".</summary>
    internal const uint CM_PROB_DISABLED = 22;

    internal const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;

    /// <summary>GUID_DEVINTERFACE_DISK — the interface a physical disk exposes.</summary>
    internal static Guid GuidDevInterfaceDisk = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    [DllImport(CfgMgr, EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    internal static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport(CfgMgr)]
    internal static extern int CM_Get_Child(out uint childDevInst, uint devInst, uint flags);

    [DllImport(CfgMgr)]
    internal static extern int CM_Get_Sibling(out uint siblingDevInst, uint devInst, uint flags);

    [DllImport(CfgMgr)]
    internal static extern int CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [DllImport(CfgMgr, EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_ID(
        uint devInst, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport(CfgMgr)]
    internal static extern int CM_Get_DevNode_Status(
        out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport(CfgMgr, EntryPoint = "CM_Get_Device_Interface_List_SizeW", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_Interface_List_Size(
        out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport(CfgMgr, EntryPoint = "CM_Get_Device_Interface_ListW", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_Interface_List(
        ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLength, uint flags);

    // ---- Disk attributes ---------------------------------------------------

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    /// <summary>CTL_CODE(IOCTL_DISK_BASE, 0x3d, METHOD_BUFFERED, READ|WRITE).</summary>
    internal const uint IOCTL_DISK_SET_DISK_ATTRIBUTES = 0x0007C0F4;

    /// <summary>CTL_CODE(IOCTL_DISK_BASE, 0x3c, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    internal const uint IOCTL_DISK_GET_DISK_ATTRIBUTES = 0x000700F0;

    /// <summary>
    /// CTL_CODE(IOCTL_DISK_BASE, 0x50, METHOD_BUFFERED, FILE_ANY_ACCESS). Makes
    /// the volume manager re-read the disk so a newly applied read-only
    /// attribute reaches an already-mounted volume.
    /// </summary>
    internal const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    internal const ulong DISK_ATTRIBUTE_OFFLINE = 0x0000000000000001;
    internal const ulong DISK_ATTRIBUTE_READ_ONLY = 0x0000000000000002;

    /// <remarks>
    /// Matches SET_DISK_ATTRIBUTES in ntddisk.h exactly: DWORD, BOOLEAN,
    /// BYTE[3], DWORDLONG, DWORDLONG, DWORD[4] — 40 bytes under natural
    /// alignment. Getting this wrong would mean setting attributes the caller
    /// never asked for, so the padding bytes are spelled out rather than implied.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SET_DISK_ATTRIBUTES
    {
        public uint Version;
        public byte Persist;
        public byte Reserved1_0;
        public byte Reserved1_1;
        public byte Reserved1_2;
        public ulong Attributes;
        public ulong AttributesMask;
        public uint Reserved2_0;
        public uint Reserved2_1;
        public uint Reserved2_2;
        public uint Reserved2_3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GET_DISK_ATTRIBUTES
    {
        public uint Version;
        public uint Reserved1;
        public ulong Attributes;
    }

    [DllImport(Kernel32, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
