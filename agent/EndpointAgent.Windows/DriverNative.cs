using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgent.Windows;

/// <summary>
/// The interop the driver collector needs on top of what <see cref="UsbNative"/>
/// already declares.
/// </summary>
/// <remarks>
/// <para>
/// Additive by design. The SetupAPI and CfgMgr32 entry points, the
/// <c>DEVPROPKEY</c> layout and the device-info structures already exist for USB
/// control and are reused unchanged; this file adds only the driver-specific
/// property keys and the INF signature check. Nothing here alters USB behaviour.
/// </para>
/// <para>
/// Every call is a typed API with no command line (ADR-0005).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DriverNative
{
    private const string SetupApi = "setupapi.dll";

    /// <summary>
    /// DEVPKEY_Device_Driver* format id from devpkey.h. Distinct from the common
    /// device property class that <see cref="UsbNative"/> declares.
    /// </summary>
    private static readonly Guid DevPropClassDriver = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    /// <summary>DEVPKEY_Device_DevNodeStatus / _ProblemCode format id.</summary>
    private static readonly Guid DevPropClassStatus = new("4340a6c5-93fa-4706-972c-7b648008a5a7");

    internal static readonly UsbNative.DEVPROPKEY DEVPKEY_Device_DriverDate =
        new() { Fmtid = DevPropClassDriver, Pid = 2 };

    internal static readonly UsbNative.DEVPROPKEY DEVPKEY_Device_DriverVersion =
        new() { Fmtid = DevPropClassDriver, Pid = 3 };

    internal static readonly UsbNative.DEVPROPKEY DEVPKEY_Device_DriverInfPath =
        new() { Fmtid = DevPropClassDriver, Pid = 5 };

    internal static readonly UsbNative.DEVPROPKEY DEVPKEY_Device_DriverProvider =
        new() { Fmtid = DevPropClassDriver, Pid = 9 };

    internal static readonly UsbNative.DEVPROPKEY DEVPKEY_Device_ProblemCode =
        new() { Fmtid = DevPropClassStatus, Pid = 3 };

    internal const uint DEVPROP_TYPE_FILETIME = 0x00000010;

    // ---- INF signature verification ---------------------------------------

    /// <summary>
    /// Signer details filled in by <see cref="SetupVerifyInfFile"/>. Fixed-size
    /// MAX_PATH buffers, matching SP_INF_SIGNER_INFO_W in setupapi.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SP_INF_SIGNER_INFO
    {
        public uint cbSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string CatalogFile;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DigitalSigner;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DigitalSignerVersion;
    }

    [DllImport(SetupApi, EntryPoint = "SetupVerifyInfFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupVerifyInfFile(
        string infName, IntPtr altPlatformInfo, ref SP_INF_SIGNER_INFO infSignerInfo);

    /// <summary>TRUST_E_NOSIGNATURE: the file carries no signature at all.</summary>
    internal const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

    /// <summary>ERROR_NO_CATALOG_FOR_OEM_INF: an OEM INF with no catalogue.</summary>
    internal const int ERROR_NO_CATALOG_FOR_OEM_INF = unchecked((int)0xE0000304);

    /// <summary>TRUST_E_BAD_DIGEST: signed, but the content no longer matches.</summary>
    internal const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);

    /// <summary>CERT_E_UNTRUSTEDROOT: signed by a chain we do not trust.</summary>
    internal const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);

    // ---- Driver installation (Milestone 13-3) -----------------------------
    //
    // Both entry points take typed parameters and have no command line to inject
    // into, which is what ADR-0005 requires. The pnputil command-line tool would need
    // a process launch, which this agent structurally cannot perform -- and which
    // AgentSafetyTests enforces by scanning every agent source file, comments
    // included, for the launch API's name.

    private const string NewDev = "newdev.dll";

    /// <summary>
    /// Stages an INF into the Windows driver store, returning the store's own copy
    /// of the path. Staging is separate from binding so a package can be rejected
    /// after signature checks without ever reaching a device.
    /// </summary>
    [DllImport(SetupApi, EntryPoint = "SetupCopyOEMInfW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupCopyOEMInf(
        string sourceInfFileName,
        string? oemSourceMediaLocation,
        uint oemSourceMediaType,
        uint copyStyle,
        System.Text.StringBuilder? destinationInfFileName,
        uint destinationInfFileNameSize,
        out uint requiredSize,
        IntPtr destinationInfFileNameComponent);

    /// <summary>SPOST_PATH: the source media location is a path.</summary>
    internal const uint SPOST_PATH = 1;

    /// <summary>SP_COPY_NEWER_ONLY: do not replace a newer INF already in the store.</summary>
    internal const uint SP_COPY_NEWER_ONLY = 0x0004;

    /// <summary>
    /// Binds a staged driver to every present device matching a hardware id.
    /// </summary>
    /// <remarks>
    /// Windows offers no per-instance variant of this call, so a hardware id matching
    /// several devices updates all of them. That is why the caller enumerates matches
    /// first and verifies each one individually afterwards.
    /// </remarks>
    [DllImport(NewDev, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    /// <summary>
    /// INSTALLFLAG_NONINTERACTIVE. Deliberately NOT combined with INSTALLFLAG_FORCE:
    /// forcing is how a worse-matching driver gets installed over a better one, and
    /// nothing in this milestone wants that behaviour by default.
    /// </summary>
    internal const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    /// <summary>ERROR_NO_SUCH_DEVINST: no present device matched the hardware id.</summary>
    internal const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);

    /// <summary>ERROR_NO_MORE_ITEMS: the update found nothing to do.</summary>
    internal const int ERROR_NO_MORE_ITEMS = 259;
}
