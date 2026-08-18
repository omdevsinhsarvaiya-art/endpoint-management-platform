using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One process from the last inventory's point-in-time snapshot (top consumers,
/// capped). Not real-time - labelled "as of CollectedAt" in the UI.
/// </summary>
public sealed class DeviceProcessEntry : AuditableEntity
{
    private DeviceProcessEntry()
    {
        Name = null!;
    }

    public DeviceProcessEntry(
        Guid deviceId, int processId, string name, long workingSetBytes, string? executablePath, DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        ProcessId = processId >= 0 ? processId : throw new ArgumentOutOfRangeException(nameof(processId));
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        WorkingSetBytes = Math.Max(0, workingSetBytes);
        ExecutablePath = Guard.OptionalMaxLength(executablePath, 512);
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }
    public int ProcessId { get; private set; }
    public string Name { get; private set; }
    public long WorkingSetBytes { get; private set; }
    public string? ExecutablePath { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; }
}
