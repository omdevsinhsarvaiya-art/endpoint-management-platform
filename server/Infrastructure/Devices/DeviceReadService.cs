using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Read-side queries for the device list and detail views.
/// </summary>
/// <remarks>
/// Projects straight to DTO shapes - list pages never materialise full entities.
/// Online/offline is computed here, per query, from heartbeat staleness; it is
/// not a stored column (see the Device entity remarks).
/// </remarks>
public sealed class DeviceReadService(
    EndpointPlatformDbContext dbContext,
    IOptions<AgentServerOptions> agentServerOptions,
    TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AgentServerOptions _options = agentServerOptions?.Value
        ?? throw new ArgumentNullException(nameof(agentServerOptions));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DevicePage> ListAsync(
        Guid organizationId,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _dbContext.Devices
            .AsNoTracking()
            .Where(d => d.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            // Parameterised by EF; the pattern characters in user input are treated
            // as literals only if escaped - escape them so "50%" matches literally.
            var escaped = term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            // Search both names. An administrator who labelled a machine "TAM0149"
            // will search for that; one who knows it as LAPTOP-LVCHEQ2H will search
            // for the hostname. Matching only one of them makes devices findable by
            // whichever name the searcher happens not to be using.
            query = query.Where(d =>
                EF.Functions.ILike(d.Hostname, $"%{escaped}%", @"\")
                || (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, $"%{escaped}%", @"\")));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var staleAfter = TimeSpan.FromSeconds(_options.OfflineAfterSeconds);
        var onlineThreshold = now - staleAfter;

        var items = await query
            .OrderByDescending(d => d.LastSeenAt)
            .ThenBy(d => d.Hostname)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DeviceListItem(
                d.Id,
                d.Hostname,
                d.DisplayName,
                d.OperatingSystem,
                d.AgentVersion,
                d.Status.ToString(),
                d.LastSeenAt,
                d.Status == Domain.Devices.DeviceStatus.Active
                    && d.LastSeenAt != null
                    && d.LastSeenAt >= onlineThreshold,
                d.EnrolledAt))
            .ToListAsync(cancellationToken);

        return new DevicePage(items, totalCount, page, pageSize);
    }

    public async Task<DeviceCounts> CountsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var onlineThreshold = now - TimeSpan.FromSeconds(_options.OfflineAfterSeconds);

        var counts = await _dbContext.Devices
            .AsNoTracking()
            .Where(d => d.OrganizationId == organizationId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Online = g.Count(d =>
                    d.Status == Domain.Devices.DeviceStatus.Active
                    && d.LastSeenAt != null
                    && d.LastSeenAt >= onlineThreshold),
                Retired = g.Count(d => d.Status == Domain.Devices.DeviceStatus.Retired),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (counts is null)
        {
            return new DeviceCounts(0, 0, 0, 0);
        }

        var offline = counts.Total - counts.Online - counts.Retired;
        return new DeviceCounts(counts.Total, counts.Online, offline, counts.Retired);
    }
}

public sealed record DeviceListItem(
    Guid Id,
    /// <summary>The Windows computer name, as reported by the agent.</summary>
    string Hostname,
    /// <summary>The administrator's console label, or null when none is set.</summary>
    string? DisplayName,
    string? OperatingSystem,
    string AgentVersion,
    string Status,
    DateTimeOffset? LastSeenAt,
    bool IsOnline,
    DateTimeOffset EnrolledAt);

public sealed record DevicePage(
    IReadOnlyList<DeviceListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record DeviceCounts(int Total, int Online, int Offline, int Retired);
