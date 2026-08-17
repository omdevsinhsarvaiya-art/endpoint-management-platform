namespace EndpointPlatform.Domain.Identity;

/// <summary>Join entity granting one <see cref="Permission"/> to one <see cref="Role"/>.</summary>
public sealed class RolePermission
{
    private RolePermission()
    {
    }

    internal RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public Guid RoleId { get; private set; }

    public Role? Role { get; private set; }

    public Guid PermissionId { get; private set; }

    public Permission? Permission { get; private set; }
}
