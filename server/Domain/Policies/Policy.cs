using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Policies;

/// <summary>
/// A named desired-state policy. Its actual desired values live in immutable
/// <see cref="PolicyVersion"/>s; the policy row carries identity and metadata.
/// </summary>
/// <remarks>
/// Policies are versioned and historical versions are never mutated
/// (spec requirement). Editing a policy creates a new version; compliance results
/// reference the exact version they were evaluated against, so history stays
/// interpretable.
/// </remarks>
public sealed class Policy : AuditableEntity
{
    private readonly List<PolicyVersion> _versions = [];

    private Policy()
    {
        Name = null!;
        Description = null!;
    }

    public Policy(Guid organizationId, PolicyType type, string name, string description)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Type = type;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
        IsEnabled = true;
    }

    public Guid OrganizationId { get; private set; }

    public PolicyType Type { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>The version currently in force (highest version number).</summary>
    public int CurrentVersionNumber { get; private set; }

    public IReadOnlyCollection<PolicyVersion> Versions => _versions.AsReadOnly();

    /// <summary>
    /// Adds a new immutable version with the given desired-state document, and
    /// makes it current. The previous version is retained untouched.
    /// </summary>
    public PolicyVersion AddVersion(string desiredStateJson, DateTimeOffset now)
    {
        var number = CurrentVersionNumber + 1;
        var version = new PolicyVersion(Id, number, desiredStateJson, now);
        _versions.Add(version);
        CurrentVersionNumber = number;
        return version;
    }

    public void Rename(string name, string description)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
    }

    public void Enable() => IsEnabled = true;

    public void Disable() => IsEnabled = false;
}
