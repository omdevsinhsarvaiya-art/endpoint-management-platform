using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One Windows local group on a managed device, with membership captured as a
/// JSON document (member name / SID / type). Replaced wholesale per upload.
/// </summary>
public sealed class DeviceLocalGroup : AuditableEntity
{
    private DeviceLocalGroup()
    {
        Sid = null!;
        Name = null!;
        MembersJson = null!;
    }

    public DeviceLocalGroup(
        Guid deviceId,
        string sid,
        string name,
        string? description,
        string membersJson,
        int memberCount,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Sid = Guard.NotNullOrWhiteSpace(sid, nameof(sid), maxLength: 184);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        Description = Guard.OptionalMaxLength(description, 512);
        MembersJson = Guard.NotNullOrWhiteSpace(membersJson, nameof(membersJson));
        MemberCount = memberCount >= 0
            ? memberCount
            : throw new ArgumentOutOfRangeException(nameof(memberCount));
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    public string Sid { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>JSON array of { name, sid, memberType }.</summary>
    public string MembersJson { get; private set; }

    public int MemberCount { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>The well-known SID of BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    public bool IsAdministratorsGroup => Sid == AdministratorsSid;
}
