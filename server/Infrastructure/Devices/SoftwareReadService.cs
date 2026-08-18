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

        // Each device contributes exactly one device_software row per (name, version,
        // publisher) - the agent collector dedupes per device and uploads the set
        // wholesale - so the group's row count is its install (device) count. This
        // shape (GroupBy -> project count -> order by count) translates to a single
        // SQL GROUP BY; a leading Distinct() does not translate here.
        var grouped = query
            .GroupBy(x => new { x.Name, x.Version, x.Publisher })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Version,
                g.Key.Publisher,
                InstallCount = g.Count(),
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
