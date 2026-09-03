using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// Reads deployments and their per-device outcomes.
/// </summary>
/// <remarks>
/// Status is <b>derived</b>, never stored. A deployment's progress is entirely a
/// function of the tasks it created, and the task is already the authority on
/// whether an install was delivered, succeeded, failed or expired. Storing a
/// second copy would drift from it, and the console would confidently show
/// progress that never happened.
/// </remarks>
public sealed class SoftwareDeploymentReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;

    public async Task<DeploymentSummaryPage> ListAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var deployments = _dbContext.SoftwareDeployments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId);

        var totalCount = await deployments.CountAsync(cancellationToken);

        var rows = await deployments
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.PackageId,
                d.PackageName,
                d.PackageVersion,
                d.TargetType,
                d.CreatedByDisplay,
                d.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(r => r.Id).ToList();

        // One query for every listed deployment's targets rather than one per
        // deployment: the list page would otherwise be an N+1 that only bites once
        // there is history to show.
        var states = await TargetStatesAsync(ids, scopedDeviceIds, cancellationToken);

        var items = rows.Select(r =>
        {
            var mine = states.Where(s => s.DeploymentId == r.Id).ToList();
            return new DeploymentSummary(
                r.Id, r.PackageId, r.PackageName, r.PackageVersion, r.TargetType.ToString(),
                r.CreatedByDisplay, r.CreatedAt, Tally(mine));
        }).ToList();

        return new DeploymentSummaryPage(items, totalCount, page, pageSize);
    }

    public async Task<DeploymentDetail?> GetAsync(
        Guid organizationId,
        Guid deploymentId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var deployment = await _dbContext.SoftwareDeployments.AsNoTracking()
            .Where(d => d.Id == deploymentId && d.OrganizationId == organizationId)
            .Select(d => new
            {
                d.Id,
                d.PackageId,
                d.PackageName,
                d.PackageVersion,
                d.TargetType,
                d.CreatedByDisplay,
                d.CreatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (deployment is null)
        {
            return null;
        }

        var query =
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join device in _dbContext.Devices.AsNoTracking() on target.DeviceId equals device.Id
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            where target.DeploymentId == deploymentId
            select new
            {
                target.DeviceId,
                device.Hostname,
                device.DisplayName,
                DeviceStatus = device.Status,
                device.LastSeenAt,
                target.State,
                target.Reason,
                target.ObservedVersion,
                target.TaskId,
                TaskStatus = (DeviceTaskStatus?)task.Status,
                task.ResultMessage,
                task.CompletedAt,
            };

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            query = query.Where(x => visible.Contains(x.DeviceId));
        }

        var rows = await query.OrderBy(x => x.Hostname).ToListAsync(cancellationToken);

        var targets = rows.Select(r => new DeploymentDeviceResult(
            r.DeviceId,
            r.Hostname,
            r.DisplayName,
            r.DeviceStatus.ToString(),
            r.LastSeenAt,
            StatusOf(r.State, r.TaskStatus),
            r.Reason.ToString(),
            r.ObservedVersion,
            r.TaskId,
            r.ResultMessage,
            r.CompletedAt)).ToList();

        return new DeploymentDetail(
            deployment.Id, deployment.PackageId, deployment.PackageName, deployment.PackageVersion,
            deployment.TargetType.ToString(), deployment.CreatedByDisplay, deployment.CreatedAt,
            TallyResults(targets), targets);
    }

    private async Task<List<TargetState>> TargetStatesAsync(
        IReadOnlyCollection<Guid> deploymentIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken)
    {
        if (deploymentIds.Count == 0)
        {
            return [];
        }

        var query =
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            where deploymentIds.Contains(target.DeploymentId)
            select new
            {
                target.DeploymentId,
                target.DeviceId,
                target.State,
                TaskStatus = (DeviceTaskStatus?)task.Status,
            };

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            query = query.Where(x => visible.Contains(x.DeviceId));
        }

        var rows = await query.ToListAsync(cancellationToken);

        return rows
            .Select(r => new TargetState(r.DeploymentId, StatusOf(r.State, r.TaskStatus)))
            .ToList();
    }

    /// <summary>
    /// The state an operator sees for one device.
    /// </summary>
    /// <remarks>
    /// A skipped target has no task and stays Skipped. Everything else follows the
    /// task, so nothing here can report progress the task does not support: a
    /// queued-but-unclaimed task is Pending, a delivered one is Installing, and an
    /// expired one is Expired rather than being quietly counted as failed.
    /// </remarks>
    private static string StatusOf(DeploymentTargetState state, DeviceTaskStatus? taskStatus)
    {
        if (state == DeploymentTargetState.Skipped)
        {
            return "Skipped";
        }

        return taskStatus switch
        {
            DeviceTaskStatus.Queued => "Pending",
            DeviceTaskStatus.Delivered => "Installing",
            DeviceTaskStatus.Succeeded => "Succeeded",
            DeviceTaskStatus.Failed => "Failed",
            DeviceTaskStatus.Expired => "Expired",
            // The task row is gone. Honest about not knowing rather than
            // presenting a default that reads like a real outcome.
            _ => "Unknown",
        };
    }

    private static DeploymentTally Tally(IEnumerable<TargetState> states)
    {
        var list = states.ToList();
        return new DeploymentTally(
            list.Count,
            list.Count(s => s.Status == "Pending"),
            list.Count(s => s.Status == "Installing"),
            list.Count(s => s.Status == "Succeeded"),
            list.Count(s => s.Status == "Failed"),
            list.Count(s => s.Status == "Expired"),
            list.Count(s => s.Status == "Skipped"));
    }

    private static DeploymentTally TallyResults(IEnumerable<DeploymentDeviceResult> results) =>
        Tally(results.Select(r => new TargetState(Guid.Empty, r.Status)));

    private sealed record TargetState(Guid DeploymentId, string Status);
}

public sealed record DeploymentTally(
    int Total, int Pending, int Installing, int Succeeded, int Failed, int Expired, int Skipped);

public sealed record DeploymentSummary(
    Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
    string CreatedByDisplay, DateTimeOffset CreatedAt, DeploymentTally Tally);

public sealed record DeploymentSummaryPage(
    IReadOnlyList<DeploymentSummary> Items, int TotalCount, int Page, int PageSize);

public sealed record DeploymentDeviceResult(
    Guid DeviceId, string Hostname, string? DisplayName, string DeviceStatus, DateTimeOffset? LastSeenAt,
    string Status, string Reason, string? ObservedVersion, Guid? TaskId, string? ResultMessage,
    DateTimeOffset? CompletedAt);

public sealed record DeploymentDetail(
    Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
    string CreatedByDisplay, DateTimeOffset CreatedAt, DeploymentTally Tally,
    IReadOnlyList<DeploymentDeviceResult> Targets);
