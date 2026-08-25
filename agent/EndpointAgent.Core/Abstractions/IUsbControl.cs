namespace EndpointAgent.Core.Abstractions;

/// <summary>How the platform classifies a USB device. Mirrors the server enum by name.</summary>
public enum UsbClass
{
    Unknown = 0,
    Storage = 1,
    Keyboard = 2,
    Mouse = 3,
    NetworkAdapter = 4,
    Hub = 5,
    Other = 6,
}

/// <summary>What the agent is enforcing on a storage device.</summary>
public enum UsbEnforcedState
{
    /// <summary>Device instance disabled: no volume, no drive letter, no access.</summary>
    Restricted = 0,

    /// <summary>Device enabled with the disk marked read-only by Windows.</summary>
    ReadOnly = 1,
}

/// <summary>One USB device as seen on the local machine.</summary>
/// <param name="InstanceId">
/// Windows device instance ID, e.g. <c>USB\VID_0781&amp;PID_5581\ABC123</c>. The
/// only identity used for policy decisions.
/// </param>
/// <param name="SerialNumber">
/// The serial from the instance ID's last segment when the device genuinely has
/// one, otherwise null. Devices without a serial get a Windows-generated
/// instance segment containing <c>&amp;</c>, which is per-port rather than
/// per-device; the enumerator reports null instead of passing that off as a
/// serial, because a grant keyed to a port would follow the port, not the stick.
/// </param>
/// <param name="IsEnabled">Whether Windows currently has the device started.</param>
public sealed record UsbDeviceInfo(
    string InstanceId,
    UsbClass Class,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string? Manufacturer,
    string? Product,
    string? HardwareIds,
    bool IsEnabled);

/// <summary>Outcome of one enforcement attempt.</summary>
/// <param name="Succeeded">
/// True only when the state was actually applied. A failure is reported to the
/// server rather than swallowed, so the console can show the device as
/// unenforced instead of implying a control that is not in place.
/// </param>
public sealed record UsbEnforcementResult(bool Succeeded, string? Error)
{
    public static readonly UsbEnforcementResult Ok = new(true, null);

    public static UsbEnforcementResult Failed(string error) => new(false, error);
}

/// <summary>Enumerates the USB devices attached to this machine.</summary>
public interface IUsbDeviceEnumerator
{
    IReadOnlyList<UsbDeviceInfo> Enumerate();
}

/// <summary>
/// Applies USB storage access state on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The Windows implementation uses SetupAPI (<c>DIF_PROPERTYCHANGE</c> with
/// <c>DICS_DISABLE</c>/<c>DICS_ENABLE</c>) and the disk IOCTL
/// <c>IOCTL_DISK_SET_DISK_ATTRIBUTES</c>. No shell, no PowerShell, no registry
/// edits through free-form commands, no kernel driver (ADR-0005).
/// </para>
/// <para>
/// There is deliberately no method that grants write access. The widest state
/// this interface can express is read-only, so no caller — and no tampered task
/// payload — can produce a writable removable disk through it.
/// </para>
/// </remarks>
public interface IUsbPolicyEnforcer
{
    /// <summary>Disables the device instance so nothing mounts. Idempotent.</summary>
    UsbEnforcementResult Restrict(string instanceId);

    /// <summary>Enables the device and marks its disks read-only. Idempotent.</summary>
    UsbEnforcementResult AllowReadOnly(string instanceId);

    /// <summary>
    /// Undoes everything this agent applied to a device, leaving it as Windows
    /// would have it with no agent installed. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because both enforcement mechanisms outlive the process that
    /// applied them. Disabling a devnode writes <c>CONFIGFLAG_DISABLED</c> into
    /// the device's registry key, which Windows honours forever — across reboots,
    /// across the service being stopped, and across the product being uninstalled.
    /// Without an explicit release, stopping the agent would leave the machine
    /// permanently altered, and uninstalling it would leave sticks disabled with
    /// no remaining mechanism to re-enable them short of Device Manager by hand.
    /// </para>
    /// <para>
    /// Release is therefore not the same as <see cref="AllowReadOnly"/>. Read-only
    /// is a state this product enforces; release is the absence of enforcement.
    /// The device comes back enabled <em>and</em> writable, because that is what
    /// an unmanaged Windows machine does with a USB stick.
    /// </para>
    /// </remarks>
    UsbEnforcementResult Release(string instanceId);
}

/// <summary>
/// Raises an event when USB devices arrive or are removed.
/// </summary>
/// <remarks>
/// Notification is an optimisation for latency, not the mechanism policy depends
/// on: the agent also reconciles on a timer, so a watcher that fails to start
/// degrades the response time from seconds to the reconcile interval rather than
/// leaving a device unmanaged.
/// </remarks>
public interface IUsbDeviceWatcher : IDisposable
{
    event EventHandler<UsbChangeKind>? Changed;

    /// <summary>Begins watching. Returns false if notifications are unavailable.</summary>
    bool TryStart();
}

public enum UsbChangeKind
{
    Arrived = 0,
    Removed = 1,
}
