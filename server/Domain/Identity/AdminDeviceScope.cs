using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// Grants one platform administrator authority over one device group.
/// </summary>
/// <remarks>
/// <para>
/// Permission answers "may this operator do X at all"; scope answers "on which
/// machines". Both must pass. A permission grant alone reaches nothing: an
/// administrator with <c>user.change_type</c> but no scope can change the account
/// type on zero devices.
/// </para>
/// <para>
/// The model is deliberately deny-by-default. A newly created administrator has no
/// scope rows and <see cref="PlatformUser.HasAllDeviceScope"/> false, so they can
/// act on nothing until an operator grants them either specific groups or
/// all-devices. Administrators that predate this model are migrated to
/// all-devices explicitly, so the upgrade cannot silently revoke anyone's access
/// and "no rows" never has to mean "unlimited".
/// </para>
/// </remarks>
public sealed class AdminDeviceScope : AuditableEntity
{
    private AdminDeviceScope()
    {
    }

    public AdminDeviceScope(Guid platformUserId, Guid deviceGroupId)
    {
        PlatformUserId = Guard.NotEmpty(platformUserId);
        DeviceGroupId = Guard.NotEmpty(deviceGroupId);
    }

    public Guid PlatformUserId { get; private set; }

    public Guid DeviceGroupId { get; private set; }
}
