using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Groups;

/// <summary>How a group's membership is determined.</summary>
public enum DeviceGroupType
{
    /// <summary>Members are added and removed explicitly.</summary>
    Static = 0,

    /// <summary>Membership is computed from a rule (Phase 13+ / future).</summary>
    Dynamic = 1,
}

/// <summary>
/// A named collection of devices. Policies, software and tasks can target a group,
/// so an assignment applies to every current member without per-device work.
/// </summary>
public sealed class DeviceGroup : AuditableEntity
{
    private DeviceGroup()
    {
        Name = null!;
        Description = null!;
    }

    public DeviceGroup(Guid organizationId, string name, string description, DeviceGroupType type)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
        Type = type;
    }

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public DeviceGroupType Type { get; private set; }

    public void Rename(string name, string description)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
    }
}
