using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Which software titles the console shows by default.
/// </summary>
/// <remarks>
/// The endpoint collects and categorises everything -- frameworks, inbox apps,
/// servicing components -- because "what runtimes are on this fleet" is a real
/// question. What an administrator wants to scroll through is applications, so
/// the default view hides the categories that are not, and an explicit request
/// shows all of them. A row an older agent reported has no category, and is
/// shown in either view rather than hidden by a label it never had.
/// </remarks>
public enum SoftwareView
{
    /// <summary>Applications: everything but frameworks, inbox apps and components.</summary>
    Applications,

    /// <summary>Everything the endpoints reported.</summary>
    All,
}

public sealed class SoftwareReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>The categories the Applications view leaves out.</summary>
    public static readonly IReadOnlyList<string> SystemCategories = ["FrameworkOrResource", "InboxApp", "Component"];

    public async Task<SoftwareTitlePage> ListTitlesAsync(
        Guid organizationId,
        int page,
        int pageSize,
        string? search,
        string? publisher,
        CancellationToken cancellationToken = default) =>
        await ListTitlesAsync(organizationId, page, pageSize, search, publisher, SoftwareView.Applications, cancellationToken);

    public async Task<SoftwareTitlePage> ListTitlesAsync(
        Guid organizationId,
        int page,
        int pageSize,
        string? search,
        string? publisher,
        SoftwareView view,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        // device_software has no organization column; scope through the device.
        var query =
            from s in _dbContext.DeviceSoftware.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on s.DeviceId equals d.Id
            where d.OrganizationId == organizationId
            select new { s.Name, s.Version, s.Publisher, s.DeviceId, s.Category, s.Confidence };

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

        if (view == SoftwareView.Applications)
        {
            var hidden = SystemCategories;
            query = query.Where(x => x.Category == null || !hidden.Contains(x.Category));
        }

        // Counted over DISTINCT devices, not rows. A device can legitimately hold
        // several rows for one (name, version, publisher): since 1.5.0 per-user
        // installs are collected, so the same product installed for three people
        // on one machine is three rows - three real installations. Counting rows
        // would report that machine three times and overstate fleet coverage,
        // which is the number an administrator makes decisions on.
        //
        // Category and confidence are per application, so every row of a title
        // normally agrees; Min picks the strongest claim where they differ (an
        // Installed row outranks an Observed one, an Application label a label
        // that hides), which is the honest summary for a title.
        var grouped = query
            .GroupBy(x => new { x.Name, x.Version, x.Publisher })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Version,
                g.Key.Publisher,
                InstallCount = g.Select(x => x.DeviceId).Distinct().Count(),
                Category = g.Min(x => x.Category),
                Confidence = g.Min(x => x.Confidence),
            });

        var totalCount = await grouped.CountAsync(cancellationToken);

        var rows = await grouped
            .OrderByDescending(t => t.InstallCount)
            .ThenBy(t => t.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new SoftwareTitle(r.Name, r.Version, r.Publisher, r.InstallCount, r.Category, r.Confidence))
            .ToList();

        return new SoftwareTitlePage(items, totalCount, page, pageSize);
    }

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
                s.IdentityKind,
                s.StableKey,
                s.Confidence,
                s.Category,
                s.PackageFamilyName,
                s.ExecutablePath,
                s.SignerSubject,
                s.SignatureStatus,
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
                r.CollectedAt,
                r.IdentityKind,
                r.StableKey,
                r.Confidence,
                r.Category,
                r.PackageFamilyName,
                r.ExecutablePath,
                r.SignerSubject,
                r.SignatureStatus))
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

/// <param name="Category">The title's category, or null when no reporting agent said (older than 1.9.0).</param>
/// <param name="Confidence"><c>Installed</c>, <c>Observed</c>, or null from older agents.</param>
public sealed record SoftwareTitle(
    string Name,
    string? Version,
    string? Publisher,
    int InstallCount,
    string? Category = null,
    string? Confidence = null);

public sealed record SoftwareTitlePage(
    IReadOnlyList<SoftwareTitle> Items, int TotalCount, int Page, int PageSize);

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
    DateTimeOffset CollectedAt,
    string? IdentityKind = null,
    string? StableKey = null,
    string? Confidence = null,
    string? Category = null,
    string? PackageFamilyName = null,
    string? ExecutablePath = null,
    string? SignerSubject = null,
    string? SignatureStatus = null);

public sealed record SoftwareInstallationPage(
    IReadOnlyList<SoftwareInstallation> Items, int TotalCount, int Page, int PageSize);
