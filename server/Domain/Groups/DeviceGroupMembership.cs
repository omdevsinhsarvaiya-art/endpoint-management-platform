using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Groups;

/// <summary>A device's membership of a static device group.</summary>
public sealed class DeviceGroupMembership : AuditableEntity
{
    private DeviceGroupMembership()
    {
    }

    public DeviceGroupMembership(Guid groupId, Guid deviceId)
    {
        GroupId = Guard.NotEmpty(groupId);
        DeviceId = Guard.NotEmpty(deviceId);
    }

    public Guid GroupId { get; private set; }
    public Guid DeviceId { get; private set; }
}
