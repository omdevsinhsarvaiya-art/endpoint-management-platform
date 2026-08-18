using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One Windows local user account on a managed device, as last reported by its
/// agent. The set is replaced wholesale per inventory upload.
/// </summary>
/// <remarks>
/// This is observed state about an endpoint — entirely distinct from
/// <see cref="Identity.PlatformUser"/> (people who sign in to this platform).
/// The SID is the stable key; names are display data. Nothing credential-shaped
/// exists on this type, and Phase 4's mutation path will not add any.
/// </remarks>
public sealed class DeviceLocalUser : AuditableEntity
{
    private DeviceLocalUser()
    {
        Sid = null!;
        Name = null!;
    }

    public DeviceLocalUser(
        Guid deviceId,
        string sid,
        string name,
        string? fullName,
        string? description,
        bool enabled,
        bool passwordRequired,
        bool passwordExpires,
        DateTimeOffset? lastLogon,
        bool isLocalAdministrator,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Sid = Guard.NotNullOrWhiteSpace(sid, nameof(sid), maxLength: 184);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        FullName = Guard.OptionalMaxLength(fullName, 256);
        Description = Guard.OptionalMaxLength(description, 512);
        Enabled = enabled;
        PasswordRequired = passwordRequired;
        PasswordExpires = passwordExpires;
        LastLogon = lastLogon;
        IsLocalAdministrator = isLocalAdministrator;
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    public string Sid { get; private set; }

    public string Name { get; private set; }

    public string? FullName { get; private set; }

    public string? Description { get; private set; }

    public bool Enabled { get; private set; }

    public bool PasswordRequired { get; private set; }

    public bool PasswordExpires { get; private set; }

    public DateTimeOffset? LastLogon { get; private set; }

    /// <summary>Member of BUILTIN\Administrators (directly), per the agent's SID check.</summary>
    public bool IsLocalAdministrator { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }
}
