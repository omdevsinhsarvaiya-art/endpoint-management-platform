using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Groups;

/// <summary>Manages device groups and their static memberships (audited).</summary>
public sealed class DeviceGroupService(
    EndpointPlatformDbContext dbContext, AuditWriter auditWriter, TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<DeviceGroup> CreateAsync(
        Guid organizationId, string name, string description,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var group = new DeviceGroup(organizationId, name, description, DeviceGroupType.Static);
        _dbContext.DeviceGroups.Add(group);

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "group.create", AuditResult.Success,
            a => a.OnTarget("device_group", group.Id.ToString(), name)
                  .Requiring(Domain.Authorization.Permissions.Group.Manage));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<bool> AddMemberAsync(
        Guid organizationId, Guid groupId, Guid deviceId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var group = await _dbContext.DeviceGroups
            .SingleOrDefaultAsync(g => g.Id == groupId && g.OrganizationId == organizationId, cancellationToken);
        var deviceExists = await _dbContext.Devices
            .AnyAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (group is null || !deviceExists)
        {
            return false;
        }

        if (await _dbContext.DeviceGroupMemberships.AnyAsync(m => m.GroupId == groupId && m.DeviceId == deviceId, cancellationToken))
        {
            return true;
        }

        _dbContext.DeviceGroupMemberships.Add(new DeviceGroupMembership(groupId, deviceId));

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "group.add_member", AuditResult.Success,
            a => a.OnDevice(deviceId, deviceId.ToString())
                  .OnTarget("device_group", groupId.ToString(), group.Name)
                  .Requiring(Domain.Authorization.Permissions.Group.Manage));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveMemberAsync(
        Guid organizationId, Guid groupId, Guid deviceId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var membership = await _dbContext.DeviceGroupMemberships
            .SingleOrDefaultAsync(m => m.GroupId == groupId && m.DeviceId == deviceId, cancellationToken);
        if (membership is null)
        {
            return false;
        }

        var groupName = await _dbContext.DeviceGroups
            .Where(g => g.Id == groupId).Select(g => g.Name).SingleOrDefaultAsync(cancellationToken) ?? groupId.ToString();

        _dbContext.DeviceGroupMemberships.Remove(membership);

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "group.remove_member", AuditResult.Success,
            a => a.OnDevice(deviceId, deviceId.ToString())
                  .OnTarget("device_group", groupId.ToString(), groupName)
                  .Requiring(Domain.Authorization.Permissions.Group.Manage));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
