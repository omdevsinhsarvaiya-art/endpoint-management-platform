using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>Windows Update status for a device (reboot flag), one row per device.</summary>
public sealed class DeviceUpdateStatus : AuditableEntity
{
    private DeviceUpdateStatus()
    {
    }

    public DeviceUpdateStatus(Guid deviceId)
    {
        DeviceId = Guard.NotEmpty(deviceId);
    }

    public Guid DeviceId { get; private set; }
    public bool RebootRequired { get; private set; }
    public int FailedUpdateCount { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; }

    public void Apply(bool rebootRequired, int failedUpdateCount, DateTimeOffset collectedAt)
    {
        RebootRequired = rebootRequired;
        FailedUpdateCount = failedUpdateCount >= 0 ? failedUpdateCount : 0;
        CollectedAt = collectedAt;
    }
}
