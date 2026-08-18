using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>One Windows service on a device, as last reported. Replaced wholesale.</summary>
public sealed class DeviceServiceEntry : AuditableEntity
{
    private DeviceServiceEntry()
    {
        Name = null!;
        DisplayName = null!;
        Status = null!;
        StartMode = null!;
    }

    public DeviceServiceEntry(
        Guid deviceId, string name, string displayName, string status, string startMode, DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName), maxLength: 384);
        Status = Guard.NotNullOrWhiteSpace(status, nameof(status), maxLength: 32);
        StartMode = Guard.NotNullOrWhiteSpace(startMode, nameof(startMode), maxLength: 32);
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }
    public string Name { get; private set; }
    public string DisplayName { get; private set; }
    public string Status { get; private set; }
    public string StartMode { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; }
}
