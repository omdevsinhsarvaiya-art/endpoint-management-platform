using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Reporting;

/// <summary>
/// A single consolidated fleet report for the management dashboard: device
/// health, patch and security posture rollups, policy compliance and task
/// throughput, all scoped to one organization.
/// </summary>
public sealed class ReportReadService(
    EndpointPlatformDbContext dbContext,
    DeviceReadService deviceReadService,
    SecurityReadService securityReadService,
    UpdateReadService updateReadService)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceReadService _deviceReadService = deviceReadService;
    private readonly SecurityReadService _securityReadService = securityReadService;
    private readonly UpdateReadService _updateReadService = updateReadService;

    public async Task<FleetReport> GetSummaryAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var devices = await _deviceReadService.CountsAsync(organizationId, cancellationToken);
        var security = (await _securityReadService.GetOverviewAsync(organizationId, cancellationToken)).Summary;
        var updates = (await _updateReadService.GetOverviewAsync(organizationId, cancellationToken)).Summary;

        var taskCountsRaw = await _dbContext.DeviceTasks
            .AsNoTracking()
            .Where(t => t.OrganizationId == organizationId)
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var byStatus = taskCountsRaw.ToDictionary(x => x.Status, x => x.Count);
        int Task(DeviceTaskStatus s) => byStatus.TryGetValue(s, out var c) ? c : 0;

        var tasks = new TaskReport(
            Queued: Task(DeviceTaskStatus.Queued),
            Delivered: Task(DeviceTaskStatus.Delivered),
            Succeeded: Task(DeviceTaskStatus.Succeeded),
            Failed: Task(DeviceTaskStatus.Failed),
            Expired: Task(DeviceTaskStatus.Expired),
            Cancelled: Task(DeviceTaskStatus.Cancelled));

        var policyTotal = await _dbContext.Policies
            .CountAsync(p => p.OrganizationId == organizationId && p.IsEnabled, cancellationToken);
        var nonCompliant = await _dbContext.PolicyComplianceResults
            .CountAsync(r => r.OrganizationId == organizationId
                && r.State == PolicyComplianceState.NonCompliant, cancellationToken);

        var activePackages = await _dbContext.SoftwarePackages
            .CountAsync(p => p.OrganizationId == organizationId && !p.IsWithdrawn, cancellationToken);

        return new FleetReport(
            devices,
            new PostureReport(
                security.DevicesReporting, security.AverageScore, security.NeedsAttention, security.Critical),
            new PatchReport(updates.DevicesReporting, updates.RebootPending, updates.WithFailedUpdates),
            new PolicyReport(policyTotal, nonCompliant),
            tasks,
            activePackages);
    }
}

public sealed record FleetReport(
    DeviceCounts Devices,
    PostureReport Security,
    PatchReport Updates,
    PolicyReport Policies,
    TaskReport Tasks,
    int ActivePackages);

public sealed record PostureReport(int DevicesReporting, int? AverageScore, int NeedsAttention, int Critical);

public sealed record PatchReport(int DevicesReporting, int RebootPending, int WithFailedUpdates);

public sealed record PolicyReport(int EnabledPolicies, int NonCompliantResults);

public sealed record TaskReport(
    int Queued, int Delivered, int Succeeded, int Failed, int Expired, int Cancelled);
