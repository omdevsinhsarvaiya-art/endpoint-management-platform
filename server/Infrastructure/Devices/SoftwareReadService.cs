using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Fleet-wide software inventory queries: the distinct installed titles across the
/// organization, with per-title install counts, searchable and publisher-filtered.
/// </summary>
public sealed class SoftwareReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<SoftwareTitlePage> ListTitlesAsync(
        Guid organizationId,
        int page,
        int pageSize,
        string? search,
        string? publisher,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        // device_software has no organization column; scope through the device.
        var query =
            from s in _dbContext.DeviceSoftware.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on s.DeviceId equals d.Id
            where d.OrganizationId == organizationId
            select new { s.Name, s.Version, s.Publisher, s.DeviceId };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var escaped = term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            query = query.Where(x => EF.Functions.ILike(x.Name, $"%{escaped}%", @"\"));
        }

        if (!string.IsNullOrWhiteSpace(publisher))
        {
            var p = publisher.Trim();
            query = query.Where(x => x.Publisher == p);
        }

        // Counted over DISTINCT devices, not rows. A device can legitimately hold
        // several rows for one (name, version, publisher): since 1.5.0 per-user
        // installs are collected, so the same product installed for three people
        // on one machine is three rows - three real installations. Counting rows
        // would report that machine three times and overstate fleet coverage,
        // which is the number an administrator makes decisions on.
        var grouped = query
            .GroupBy(x => new { x.Name, x.Version, x.Publisher })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Version,
                g.Key.Publisher,
                InstallCount = g.Select(x => x.DeviceId).Distinct().Count(),
            });

        var totalCount = await grouped.CountAsync(cancellationToken);

        var rows = await grouped
            .OrderByDescending(t => t.InstallCount)
            .ThenBy(t => t.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new SoftwareTitle(r.Name, r.Version, r.Publisher, r.InstallCount))
            .ToList();

        return new SoftwareTitlePage(items, totalCount, page, pageSize);
    }

    /// <summary>
    /// The devices a given title is installed on, for the inventory drill-down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Device-scoped, unlike the title aggregate above: a count discloses nothing
    /// about which machines an administrator may not see, but a list of hostnames
    /// does. <paramref name="scopedDeviceIds"/> is null for an unrestricted
    /// administrator and otherwise the exact set they may see, so scope narrows
    /// the result rather than revealing that a device exists.
    /// </para>
    /// <para>
    /// A title is identified by the same triple the aggregate groups on. Version
    /// and publisher are matched including their absence -- a null version is a
    /// real, distinct title, not a wildcard -- so drilling into a row returns that
    /// row's devices and not a superset.
    /// </para>
    /// <para>
    /// One row per installation, so a device appears once per user who has the
    /// product. That is the truth of the machine and it is what makes a per-user
    /// deployment decision possible; the device count above is DISTINCT.
    /// </para>
    /// </remarks>
    public async Task<SoftwareInstallationPage> ListInstallationsAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        string name,
        string? version,
        string? publisher,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query =
            from s in _dbContext.DeviceSoftware.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on s.DeviceId equals d.Id
            where d.OrganizationId == organizationId
                && s.Name == name
                && s.Version == version
                && s.Publisher == publisher
            select new
            {
                s.DeviceId,
                d.Hostname,
                d.DisplayName,
                DeviceStatus = d.Status,
                d.LastSeenAt,
                s.InstallationScope,
                s.InstalledForUser,
                s.Architecture,
                s.InstallLocation,
                s.ProductCode,
                s.CollectedAt,
            };

        if (scopedDeviceIds is not null)
        {
            query = query.Where(x => scopedDeviceIds.Contains(x.DeviceId));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderBy(x => x.Hostname)
            .ThenBy(x => x.InstalledForUser)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new SoftwareInstallation(
                r.DeviceId,
                r.Hostname,
                r.DisplayName,
                r.DeviceStatus.ToString(),
                r.LastSeenAt,
                r.InstallationScope,
                r.InstalledForUser,
                r.Architecture,
                r.InstallLocation,
                r.ProductCode,
                r.CollectedAt))
            .ToList();

        return new SoftwareInstallationPage(items, totalCount, page, pageSize);
    }

    public async Task<IReadOnlyList<string>> ListPublishersAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await (
            from s in _dbContext.DeviceSoftware.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on s.DeviceId equals d.Id
            where d.OrganizationId == organizationId && s.Publisher != null
            select s.Publisher!)
            .Distinct()
            .OrderBy(p => p)
            .Take(500)
            .ToListAsync(cancellationToken);
}

public sealed record SoftwareTitle(string Name, string? Version, string? Publisher, int InstallCount);

public sealed record SoftwareTitlePage(
    IReadOnlyList<SoftwareTitle> Items, int TotalCount, int Page, int PageSize);

/// <summary>One installation of a title on one device.</summary>
/// <remarks>
/// Carries <see cref="DeviceId"/> as the identity: hostnames are display text,
/// they repeat across a fleet and they change, so nothing addresses a device by
/// one.
/// </remarks>
public sealed record SoftwareInstallation(
    Guid DeviceId,
    string Hostname,
    string? DisplayName,
    string DeviceStatus,
    DateTimeOffset? LastSeenAt,
    string? InstallationScope,
    string? InstalledForUser,
    string? Architecture,
    string? InstallLocation,
    string? ProductCode,
    DateTimeOffset CollectedAt);

public sealed record SoftwareInstallationPage(
    IReadOnlyList<SoftwareInstallation> Items, int TotalCount, int Page, int PageSize);
