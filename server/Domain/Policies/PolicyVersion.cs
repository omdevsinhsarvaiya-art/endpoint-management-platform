using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Policies;

/// <summary>
/// One immutable version of a policy's desired state.
/// </summary>
/// <remarks>
/// Immutable by construction: no mutators, all private setters. A policy edit adds
/// a new version rather than changing this one, so a compliance result recorded
/// against version N always describes exactly the state that was evaluated.
/// </remarks>
public sealed class PolicyVersion : Entity
{
    private PolicyVersion()
    {
        DesiredStateJson = null!;
    }

    internal PolicyVersion(Guid policyId, int versionNumber, string desiredStateJson, DateTimeOffset createdAt)
    {
        PolicyId = Guard.NotEmpty(policyId);
        VersionNumber = versionNumber > 0 ? versionNumber : throw new ArgumentOutOfRangeException(nameof(versionNumber));
        DesiredStateJson = Guard.NotNullOrWhiteSpace(desiredStateJson, nameof(desiredStateJson), maxLength: 8192);
        CreatedAt = createdAt;
    }

    public Guid PolicyId { get; private set; }

    public int VersionNumber { get; private set; }

    /// <summary>The desired-state document (jsonb), specific to the policy type.</summary>
    public string DesiredStateJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
