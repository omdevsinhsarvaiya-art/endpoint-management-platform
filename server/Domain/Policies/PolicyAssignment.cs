using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Policies;

/// <summary>What a policy assignment targets.</summary>
public enum PolicyAssignmentTarget
{
    Device = 0,
    Group = 1,
}

/// <summary>Assigns a policy to a device or a device group.</summary>
public sealed class PolicyAssignment : AuditableEntity
{
    private PolicyAssignment()
    {
    }

    public PolicyAssignment(Guid organizationId, Guid policyId, PolicyAssignmentTarget targetType, Guid targetId)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        PolicyId = Guard.NotEmpty(policyId);
        TargetType = targetType;
        TargetId = Guard.NotEmpty(targetId);
    }

    public Guid OrganizationId { get; private set; }
    public Guid PolicyId { get; private set; }
    public PolicyAssignmentTarget TargetType { get; private set; }

    /// <summary>Device id or group id, per <see cref="TargetType"/>.</summary>
    public Guid TargetId { get; private set; }
}
