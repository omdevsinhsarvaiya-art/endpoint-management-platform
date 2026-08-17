using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// A single privilege, identified by a stable dotted key such as <c>device.restart</c>.
/// </summary>
/// <remarks>
/// Rows are seeded from <see cref="Authorization.Permissions.All"/> and are never
/// created at runtime by an administrator. The key is the contract shared with the
/// API's authorisation policies; changing one is a breaking change and requires a
/// migration that re-points existing role grants.
/// </remarks>
public sealed class Permission : AuditableEntity
{
    private Permission()
    {
        Key = null!;
        Category = null!;
        Description = null!;
    }

    public Permission(string key, string category, string description, bool isHighRisk)
    {
        Key = Guard.NotNullOrWhiteSpace(key, nameof(key), maxLength: 64).ToLowerInvariant();
        Category = Guard.NotNullOrWhiteSpace(category, nameof(category), maxLength: 64);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
        IsHighRisk = isHighRisk;
    }

    public string Key { get; private set; }

    public string Category { get; private set; }

    public string Description { get; private set; }

    /// <summary>
    /// Marks a permission whose use changes security posture on an endpoint.
    /// High-risk actions require explicit confirmation in the UI and are recorded
    /// in the audit log at elevated severity.
    /// </summary>
    public bool IsHighRisk { get; private set; }

    /// <summary>
    /// Reconciles descriptive metadata with the code catalogue during seeding.
    /// Note what cannot be changed: <see cref="Key"/> is immutable, because it is
    /// the identifier that role grants and API authorisation policies are bound to.
    /// </summary>
    public void UpdateMetadata(string category, string description, bool isHighRisk)
    {
        Category = Guard.NotNullOrWhiteSpace(category, nameof(category), maxLength: 64);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
        IsHighRisk = isHighRisk;
    }
}
