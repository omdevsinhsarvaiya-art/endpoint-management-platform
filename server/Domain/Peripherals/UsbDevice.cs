using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Peripherals;

/// <summary>
/// How the platform classifies a USB device. Only <see cref="Storage"/> is
/// subject to access policy; everything else is inventory-only.
/// </summary>
/// <remarks>
/// Applying storage policy to a keyboard would lock an administrator out of
/// their own machine, so the classes are kept explicitly separate and the
/// policy path checks <see cref="UsbDevice.IsStorage"/> rather than inferring
/// from a name.
/// </remarks>
public enum UsbDeviceClass
{
    /// <summary>Windows reported a class the platform does not model.</summary>
    Unknown = 0,

    /// <summary>Removable mass storage. The only class policy applies to.</summary>
    Storage = 1,

    Keyboard = 2,
    Mouse = 3,

    /// <summary>USB network adapter.</summary>
    NetworkAdapter = 4,

    Hub = 5,

    /// <summary>Audio, imaging, biometric, printers and anything else present.</summary>
    Other = 6,
}

/// <summary>Access state for a USB storage device on one endpoint.</summary>
public enum UsbStoragePolicy
{
    /// <summary>
    /// The default and the safe state: no file access. The device instance is
    /// disabled on the endpoint, so no drive letter appears.
    /// </summary>
    Restricted = 0,

    /// <summary>
    /// Temporary administrator-granted read access. Files can be read and
    /// copied off the device; writes, creates, renames and deletes are refused
    /// by Windows itself.
    /// </summary>
    ReadOnly = 1,

    /// <summary>
    /// Temporary administrator-granted read/write access: ordinary Windows
    /// behaviour for the duration of the grant.
    /// </summary>
    /// <remarks>
    /// The widest state the platform can express, and the only one that permits
    /// writing to removable media. It is time-boxed and audited exactly like
    /// <see cref="ReadOnly"/> — it is not a way to mark a device permanently
    /// trusted, and it still returns to <see cref="Restricted"/> the moment the
    /// grant lapses or is revoked.
    /// </remarks>
    Enabled = 2,
}

/// <summary>
/// A USB peripheral as last reported by an endpoint's agent.
/// </summary>
/// <remarks>
/// <para>
/// Identity is the Windows device instance ID — <c>USB\VID_0781&amp;PID_5581\ABC123</c>
/// — which carries vendor, product and (when the device has one) serial. The
/// friendly name is presentation only and is never used to match a device,
/// because it is attacker-chosen: a USB stick can call itself anything.
/// </para>
/// <para>
/// A device with no serial number gets none. The platform does not synthesise
/// one, because a fabricated identity would silently make two different sticks
/// look like the same approved device.
/// </para>
/// </remarks>
public sealed class UsbDevice : AuditableEntity
{
    private UsbDevice()
    {
        InstanceId = null!;
    }

    public UsbDevice(
        Guid organizationId,
        Guid deviceId,
        string instanceId,
        UsbDeviceClass deviceClass,
        string? vendorId,
        string? productId,
        string? serialNumber,
        string? manufacturer,
        string? product,
        string? hardwareIds,
        DateTimeOffset now)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceId = Guard.NotEmpty(deviceId);
        InstanceId = Guard.NotNullOrWhiteSpace(instanceId, nameof(instanceId), maxLength: 512);
        DeviceClass = deviceClass;
        VendorId = Guard.OptionalMaxLength(vendorId, 8);
        ProductId = Guard.OptionalMaxLength(productId, 8);
        SerialNumber = Guard.OptionalMaxLength(serialNumber, 128);
        Manufacturer = Guard.OptionalMaxLength(manufacturer, 256);
        Product = Guard.OptionalMaxLength(product, 256);
        HardwareIds = Guard.OptionalMaxLength(hardwareIds, 1024);
        FirstSeenAt = now;
        LastSeenAt = now;
        IsConnected = true;

        // Storage starts Restricted, always. Nothing in the constructor can
        // produce a device that is accessible before an administrator says so.
        Policy = UsbStoragePolicy.Restricted;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>Windows device instance ID. The identity the agent enforces against.</summary>
    public string InstanceId { get; private set; }

    public UsbDeviceClass DeviceClass { get; private set; }

    /// <summary>Four hex digits, e.g. <c>0781</c>. Null when not a VID/PID device.</summary>
    public string? VendorId { get; private set; }

    public string? ProductId { get; private set; }

    /// <summary>Device serial, when Windows exposes one. Never invented.</summary>
    public string? SerialNumber { get; private set; }

    public string? Manufacturer { get; private set; }

    public string? Product { get; private set; }

    /// <summary>Semicolon-joined hardware IDs, for diagnostics.</summary>
    public string? HardwareIds { get; private set; }

    public bool IsConnected { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset? DisconnectedAt { get; private set; }

    /// <summary>Current access state. Meaningful only when <see cref="IsStorage"/>.</summary>
    public UsbStoragePolicy Policy { get; private set; }

    /// <summary>When a temporary grant lapses. Null whenever Restricted.</summary>
    public DateTimeOffset? PolicyExpiresAt { get; private set; }

    /// <summary>
    /// What the endpoint last reported it is <em>actually</em> enforcing, as
    /// opposed to <see cref="Policy"/>, which is what the console has decided.
    /// Null until the agent has confirmed anything.
    /// </summary>
    /// <remarks>
    /// These are kept separate deliberately. A console that renders the desired
    /// state as though it were the enforced state will show "Restricted" for a
    /// machine that is offline, has not applied the policy yet, or where a local
    /// administrator re-enabled the device in Device Manager. The difference
    /// between the two fields is exactly the information an operator needs, so
    /// it is preserved rather than collapsed.
    /// </remarks>
    public UsbStoragePolicy? EnforcedPolicy { get; private set; }

    public DateTimeOffset? EnforcedAt { get; private set; }

    /// <summary>What went wrong the last time the agent tried to enforce. Null when it worked.</summary>
    public string? EnforcementError { get; private set; }

    /// <summary>True when the endpoint has confirmed it is enforcing what was asked.</summary>
    public bool IsPolicyEnforced => EnforcedPolicy == Policy && EnforcementError is null;

    /// <summary>True when access policy applies to this device at all.</summary>
    public bool IsStorage => DeviceClass == UsbDeviceClass.Storage;

    /// <summary>
    /// True when a grant of any kind is currently live. Expiry is evaluated
    /// against the clock rather than stored as a flag, so a grant cannot outlive
    /// its deadline because a sweep did not run.
    /// </summary>
    public bool HasLiveGrant(DateTimeOffset now) =>
        Policy != UsbStoragePolicy.Restricted && PolicyExpiresAt is { } expiry && expiry > now;

    /// <summary>Records a fresh sighting from an agent report.</summary>
    public void Seen(
        UsbDeviceClass deviceClass,
        string? manufacturer,
        string? product,
        string? hardwareIds,
        DateTimeOffset now)
    {
        DeviceClass = deviceClass;
        Manufacturer = Guard.OptionalMaxLength(manufacturer, 256) ?? Manufacturer;
        Product = Guard.OptionalMaxLength(product, 256) ?? Product;
        HardwareIds = Guard.OptionalMaxLength(hardwareIds, 1024) ?? HardwareIds;
        LastSeenAt = now;
        IsConnected = true;
        DisconnectedAt = null;
    }

    public void Disconnected(DateTimeOffset now)
    {
        // Policy deliberately survives disconnection. Re-plugging a stick must
        // not be a way to shed a Restricted state, and an unexpired grant should
        // still be honoured if the same device comes back before it lapses.
        IsConnected = false;
        DisconnectedAt = now;
    }

    /// <summary>Grants temporary access at the given level. Storage only.</summary>
    /// <param name="policy">
    /// <see cref="UsbStoragePolicy.ReadOnly"/> or <see cref="UsbStoragePolicy.Enabled"/>.
    /// Passing <see cref="UsbStoragePolicy.Restricted"/> throws: restricting is
    /// the absence of a grant, expressed through <see cref="Restrict"/>, and
    /// letting it in here would make "grant" a verb that can also take access
    /// away — with an expiry attached to a state that has none.
    /// </param>
    public void Grant(UsbStoragePolicy policy, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (!IsStorage)
        {
            throw new InvalidOperationException(
                $"Access policy applies to storage devices only; {InstanceId} is {DeviceClass}.");
        }

        if (policy == UsbStoragePolicy.Restricted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy), "Restricted is the absence of a grant; use Restrict().");
        }

        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), $"Unknown USB storage policy {policy}.");
        }

        if (expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A grant must expire in the future.");
        }

        Policy = policy;
        PolicyExpiresAt = expiresAt;
    }

    /// <summary>Returns the device to the default state. Idempotent.</summary>
    public void Restrict()
    {
        Policy = UsbStoragePolicy.Restricted;
        PolicyExpiresAt = null;
    }

    /// <summary>
    /// Records what the endpoint says it is enforcing right now. Reported by the
    /// agent on every USB report, not just after a policy task, so that drift —
    /// a local administrator re-enabling the device by hand — surfaces on the
    /// next report instead of never.
    /// </summary>
    public void ReportEnforcement(UsbStoragePolicy? enforced, string? error, DateTimeOffset now)
    {
        EnforcedPolicy = enforced;
        EnforcementError = Guard.OptionalMaxLength(error, 512);
        EnforcedAt = now;
    }
}
