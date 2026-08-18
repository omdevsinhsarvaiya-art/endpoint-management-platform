using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>Fleet Windows Update overview: reboot-pending and failed-update counts.</summary>
public sealed class UpdateReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<UpdateOverview> GetOverviewAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var rows = await (
            from u in _dbContext.DeviceUpdateStatus.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on u.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new DeviceUpdateSummary(u.DeviceId, d.Hostname, u.RebootRequired, u.FailedUpdateCount, u.CollectedAt))
            .ToListAsync(cancellationToken);

        var summary = new UpdateSummary(
            rows.Count,
            rows.Count(r => r.RebootRequired),
            rows.Count(r => r.FailedUpdateCount > 0));

        return new UpdateOverview(summary, rows
            .OrderByDescending(r => r.RebootRequired)
            .ThenByDescending(r => r.FailedUpdateCount)
            .ThenBy(r => r.Hostname)
            .ToList());
    }
}

public sealed record DeviceUpdateSummary(
    Guid DeviceId, string Hostname, bool RebootRequired, int FailedUpdateCount, DateTimeOffset CollectedAt);

public sealed record UpdateSummary(int DevicesReporting, int RebootPending, int WithFailedUpdates);

public sealed record UpdateOverview(UpdateSummary Summary, IReadOnlyList<DeviceUpdateSummary> Devices);
