using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Policies;

/// <summary>Compliance state of a device against a policy version.</summary>
public enum PolicyComplianceState
{
    Compliant = 0,
    NonCompliant = 1,

    /// <summary>The agent could not evaluate the policy (e.g. value unreadable).</summary>
    Unknown = 2,
}

/// <summary>
/// The latest evaluation of one device against one policy. One row per
/// (device, policy): a new report for the same pair updates it in place.
/// </summary>
public sealed class PolicyComplianceResult : AuditableEntity
{
    private PolicyComplianceResult()
    {
    }

    public PolicyComplianceResult(Guid organizationId, Guid deviceId, Guid policyId)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceId = Guard.NotEmpty(deviceId);
        PolicyId = Guard.NotEmpty(policyId);
        State = PolicyComplianceState.Unknown;
    }

    public Guid OrganizationId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid PolicyId { get; private set; }

    /// <summary>The exact version the device was evaluated against.</summary>
    public Guid PolicyVersionId { get; private set; }
    public int PolicyVersionNumber { get; private set; }

    public PolicyComplianceState State { get; private set; }

    /// <summary>Human-readable deviations (jsonb array), empty when compliant.</summary>
    public string? DeviationsJson { get; private set; }

    public DateTimeOffset EvaluatedAt { get; private set; }

    public void Record(
        Guid policyVersionId, int versionNumber, PolicyComplianceState state,
        string? deviationsJson, DateTimeOffset evaluatedAt)
    {
        PolicyVersionId = Guard.NotEmpty(policyVersionId);
        PolicyVersionNumber = versionNumber;
        State = state;
        DeviationsJson = deviationsJson;
        EvaluatedAt = evaluatedAt;
    }
}
