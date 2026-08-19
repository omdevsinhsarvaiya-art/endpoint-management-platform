using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Answers "may this administrator act on this device?" — the scope half of
/// authorization, enforced server-side alongside the permission check.
/// </summary>
/// <remarks>
/// <para>
/// A permission grant says what an operator may do; scope says where. Both are
/// required, and both are checked on the server: the dashboard hides controls as a
/// courtesy, never as a boundary.
/// </para>
/// <para>
/// The model is deny-by-default. Authority comes from exactly two sources:
/// <see cref="Domain.Identity.PlatformUser.HasAllDeviceScope"/>, or an
/// <see cref="Domain.Identity.AdminDeviceScope"/> row naming a device group the
/// device belongs to. An administrator with neither reaches no device at all — "no
/// scope rows" means nothing, never everything.
/// </para>
/// <para>
/// Organization tenancy is checked here too, so a scoped lookup can never leak a
/// device from another tenant even if a group id were guessed.
/// </para>
/// </remarks>
public sealed class DeviceScopeAuthorizer(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>True when the administrator may act on the device.</summary>
    public async Task<bool> CanActOnDeviceAsync(
        Guid platformUserId,
        Guid organizationId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        // The device must exist in the caller's organization at all.
        var deviceExists = await _dbContext.Devices
            .AnyAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (!deviceExists)
        {
            return false;
        }

        var hasAllScope = await _dbContext.PlatformUsers
            .Where(u => u.Id == platformUserId && u.OrganizationId == organizationId)
            .Select(u => u.HasAllDeviceScope)
            .SingleOrDefaultAsync(cancellationToken);
        if (hasAllScope)
        {
            return true;
        }

        // Otherwise the device must be a member of a group this administrator is scoped to.
        return await (
            from scope in _dbContext.AdminDeviceScopes
            join membership in _dbContext.DeviceGroupMemberships
                on scope.DeviceGroupId equals membership.GroupId
            where scope.PlatformUserId == platformUserId && membership.DeviceId == deviceId
            select scope.Id)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Restricts a device query to those the administrator may see. Used by list/read
    /// endpoints so scope narrows results rather than leaking a device's existence.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> ScopedDeviceIdsOrNullAsync(
        Guid platformUserId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var hasAllScope = await _dbContext.PlatformUsers
            .Where(u => u.Id == platformUserId && u.OrganizationId == organizationId)
            .Select(u => u.HasAllDeviceScope)
            .SingleOrDefaultAsync(cancellationToken);

        // null means "unrestricted" — the caller applies no additional filter.
        if (hasAllScope)
        {
            return null;
        }

        return await (
            from scope in _dbContext.AdminDeviceScopes
            join membership in _dbContext.DeviceGroupMemberships
                on scope.DeviceGroupId equals membership.GroupId
            where scope.PlatformUserId == platformUserId
            select membership.DeviceId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
