using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// Hardware facts for one device, replaced wholesale on each inventory upload.
/// </summary>
/// <remarks>
/// One row per device (unique index). Fixed, queryable columns for the facts the
/// dashboard filters and sorts on; disks alone are a JSON document because their
/// count varies per machine and no current query needs to filter on an individual
/// volume. Everything here is agent-reported and treated as display data, not
/// identity.
/// </remarks>
public sealed class DeviceHardware : AuditableEntity
{
    private DeviceHardware()
    {
    }

    public DeviceHardware(Guid deviceId)
    {
        DeviceId = Guard.NotEmpty(deviceId);
    }

    public Guid DeviceId { get; private set; }

    public string? SerialNumber { get; private set; }

    public string? Manufacturer { get; private set; }

    public string? Model { get; private set; }

    public string? CpuName { get; private set; }

    public int? CpuPhysicalCores { get; private set; }

    public int? CpuLogicalProcessors { get; private set; }

    public long? TotalMemoryBytes { get; private set; }

    /// <summary>JSON array of volumes: name, filesystem, sizeBytes, freeBytes.</summary>
    public string? DisksJson { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    public void Apply(
        string? serialNumber,
        string? manufacturer,
        string? model,
        string? cpuName,
        int? cpuPhysicalCores,
        int? cpuLogicalProcessors,
        long? totalMemoryBytes,
        string? disksJson,
        DateTimeOffset collectedAt)
    {
        SerialNumber = Guard.OptionalMaxLength(serialNumber, 128);
        Manufacturer = Guard.OptionalMaxLength(manufacturer, 128);
        Model = Guard.OptionalMaxLength(model, 128);
        CpuName = Guard.OptionalMaxLength(cpuName, 128);

        CpuPhysicalCores = ValidateCount(cpuPhysicalCores, nameof(cpuPhysicalCores));
        CpuLogicalProcessors = ValidateCount(cpuLogicalProcessors, nameof(cpuLogicalProcessors));

        if (totalMemoryBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalMemoryBytes));
        }

        TotalMemoryBytes = totalMemoryBytes;
        DisksJson = disksJson;
        CollectedAt = collectedAt;
    }

    private static int? ValidateCount(int? value, string paramName)
    {
        if (value is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Count is outside a plausible range.");
        }

        return value;
    }
}
