using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// A named bundle of permissions that can be assigned to platform users.
/// </summary>
/// <remarks>
/// Roles exist purely as a convenience for granting permissions. No authorisation
/// decision anywhere in the codebase inspects a role — decisions are made against
/// the permission set the roles resolve to.
/// </remarks>
public sealed class Role : AuditableEntity
{
    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
        Key = null!;
        DisplayName = null!;
        Description = null!;
    }

    private Role(Guid? organizationId, string key, string displayName, string description, bool isBuiltIn)
    {
        OrganizationId = organizationId;
        Key = Guard.NotNullOrWhiteSpace(key, nameof(key), maxLength: 64).ToLowerInvariant();
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName), maxLength: 128);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), maxLength: 512);
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// Creates one of the four built-in roles. Built-in roles are global
    /// (<see cref="OrganizationId"/> is null), cannot be deleted, and have their
    /// permission set reconciled against <see cref="Authorization.SystemRoles"/> on
    /// every startup.
    /// </summary>
    public static Role CreateBuiltIn(string key, string displayName, string description) =>
        new(organizationId: null, key, displayName, description, isBuiltIn: true);

    /// <summary>Creates a custom, organization-scoped role.</summary>
    public static Role CreateCustom(Guid organizationId, string key, string displayName, string description) =>
        new(Guard.NotEmpty(organizationId), key, displayName, description, isBuiltIn: false);

    /// <summary>Null for built-in roles, which are shared by every organization.</summary>
    public Guid? OrganizationId { get; private set; }

    public string Key { get; private set; }

    public string DisplayName { get; private set; }

    public string Description { get; private set; }

    public bool IsBuiltIn { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public void GrantPermission(Guid permissionId)
    {
        Guard.NotEmpty(permissionId);

        if (_permissions.Any(p => p.PermissionId == permissionId))
        {
            return;
        }

        _permissions.Add(new RolePermission(Id, permissionId));
    }

    public void RevokePermission(Guid permissionId) =>
        _permissions.RemoveAll(p => p.PermissionId == permissionId);
}
