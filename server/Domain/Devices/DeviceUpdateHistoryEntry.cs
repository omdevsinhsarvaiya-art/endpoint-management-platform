using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>One Windows Update history entry for a device. Replaced wholesale.</summary>
public sealed class DeviceUpdateHistoryEntry : AuditableEntity
{
    private DeviceUpdateHistoryEntry()
    {
        Title = null!;
        Operation = null!;
        Result = null!;
    }

    public DeviceUpdateHistoryEntry(
        Guid deviceId, string title, DateTimeOffset? date, string operation, string result, DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), maxLength: 384);
        Date = date;
        Operation = Guard.NotNullOrWhiteSpace(operation, nameof(operation), maxLength: 32);
        Result = Guard.NotNullOrWhiteSpace(result, nameof(result), maxLength: 32);
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }
    public string Title { get; private set; }
    public DateTimeOffset? Date { get; private set; }
    public string Operation { get; private set; }
    public string Result { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; }

    public bool IsFailure => Result is "Failed" or "Aborted";
}
