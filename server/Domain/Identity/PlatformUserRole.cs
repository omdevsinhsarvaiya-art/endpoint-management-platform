namespace EndpointPlatform.Domain.Identity;

/// <summary>Join entity assigning one <see cref="Role"/> to one <see cref="PlatformUser"/>.</summary>
public sealed class PlatformUserRole
{
    private PlatformUserRole()
    {
    }

    internal PlatformUserRole(Guid platformUserId, Guid roleId)
    {
        PlatformUserId = platformUserId;
        RoleId = roleId;
    }

    public Guid PlatformUserId { get; private set; }

    public PlatformUser? PlatformUser { get; private set; }

    public Guid RoleId { get; private set; }

    public Role? Role { get; private set; }
}
