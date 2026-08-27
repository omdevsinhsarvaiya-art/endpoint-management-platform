using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One PnP device and its driver on a managed endpoint, as last reported.
/// Replaced wholesale per inventory upload, like software and services.
/// </summary>
/// <remarks>
/// <para>
/// Stores only what the endpoint reported. The health verdict is deliberately not
/// a column: it is derived by <see cref="DriverHealth"/> on read, so re-classifying
/// a problem code never needs a migration and a stored row can never disagree with
/// the current classification.
/// </para>
/// <para>
/// Every driver-describing field is nullable. A device with no driver bound reports
/// no version, provider or date, and that is a fact about the machine rather than a
/// gap to be filled in with a guess.
/// </para>
/// </remarks>
public sealed class DeviceDriver : AuditableEntity
{
    private DeviceDriver()
    {
        InstanceId = null!;
        DeviceName = null!;
    }

    public DeviceDriver(
        Guid deviceId,
        string instanceId,
        string deviceName,
        string? deviceClass,
        string? manufacturer,
        string? driverProvider,
        string? driverVersion,
        DateTimeOffset? driverDate,
        string? infName,
        int? problemCode,
        bool? isSigned,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        InstanceId = Guard.NotNullOrWhiteSpace(instanceId, nameof(instanceId), maxLength: 512);
        DeviceName = Guard.NotNullOrWhiteSpace(deviceName, nameof(deviceName), maxLength: 384);
        DeviceClass = Guard.OptionalMaxLength(deviceClass, 128);
        Manufacturer = Guard.OptionalMaxLength(manufacturer, 256);
        DriverProvider = Guard.OptionalMaxLength(driverProvider, 256);
        DriverVersion = Guard.OptionalMaxLength(driverVersion, 64);
        DriverDate = driverDate;
        InfName = Guard.OptionalMaxLength(infName, 256);
        ProblemCode = problemCode;
        IsSigned = isSigned;
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>The PnP instance id. Stable identity of the devnode on this machine.</summary>
    public string InstanceId { get; private set; }

    public string DeviceName { get; private set; }

    /// <summary>The setup class name, e.g. "Net", "Display", "USB".</summary>
    public string? DeviceClass { get; private set; }

    public string? Manufacturer { get; private set; }

    /// <summary>Who published the bound driver, which is not necessarily the hardware vendor.</summary>
    public string? DriverProvider { get; private set; }

    public string? DriverVersion { get; private set; }

    public DateTimeOffset? DriverDate { get; private set; }

    /// <summary>The bound INF, e.g. "oem42.inf". Identifies the driver package.</summary>
    public string? InfName { get; private set; }

    /// <summary>
    /// The Windows PnP problem code: 0 for none, null when the endpoint could not
    /// read it. Null is not zero -- see <see cref="DriverHealthState.Unknown"/>.
    /// </summary>
    public int? ProblemCode { get; private set; }

    /// <summary>
    /// Whether the bound driver package carries a valid digital signature. Null
    /// when it could not be determined reliably, which is common and is reported
    /// as unknown rather than assumed either way.
    /// </summary>
    public bool? IsSigned { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>This row as the view the health rollup consumes.</summary>
    public DriverView ToView() => new(InstanceId, DeviceName, DeviceClass, ProblemCode);
}
