using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// Stops a named installed application on one or more devices.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client names an application, never a process.</b> The request carries
/// the application's display name and publisher -- values that already exist in
/// this platform's own inventory -- and the server resolves those to concrete
/// process ids itself. Accepting an image name or executable path from the
/// browser would be accepting an instruction to terminate an arbitrary process,
/// which is precisely the capability the typed-task architecture exists to deny.
/// </para>
/// <para>
/// Resolution is by install path, not by name: see
/// <see cref="ApplicationProcessMatcher"/> for why a display name cannot be
/// turned into an image name safely. An application that cannot be resolved
/// produces no tasks and says so, rather than guessing.
/// </para>
/// <para>
/// Termination itself is the existing <see cref="DeviceTaskType.TerminateProcess"/>
/// task, queued through <see cref="DeviceTaskService"/>. Nothing new executes on
/// the endpoint: the same executor, the same PID-reuse guard, the same refusal to
/// touch system processes. This service only decides which pids to name.
/// </para>
/// </remarks>
public sealed class ApplicationForceStopService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;

    public async Task<ForceStopResult> StopAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> deviceIds,
        string applicationName,
        string? publisher,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var targeted = deviceIds.ToHashSet();

        // Tenancy first: a device id from another organization simply does not
        // come back, so it can never be named in a task.
        var devices = await _dbContext.Devices
            .AsNoTracking()
            .Where(d => targeted.Contains(d.Id) && d.OrganizationId == organizationId)
            .Select(d => new { d.Id, d.Hostname })
            .ToListAsync(cancellationToken);

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            devices = devices.Where(d => visible.Contains(d.Id)).ToList();
        }

        if (devices.Count == 0)
        {
            return new ForceStopResult([], 0);
        }

        var deviceIdSet = devices.Select(d => d.Id).ToHashSet();

        // Two queries for the whole request, not two per device.
        var software = await _dbContext.DeviceSoftware
            .AsNoTracking()
            .Where(s => deviceIdSet.Contains(s.DeviceId) && s.Name == applicationName)
            .Select(s => new { s.DeviceId, s.Name, s.Publisher, s.InstallLocation })
            .ToListAsync(cancellationToken);

        var processes = await _dbContext.DeviceProcesses
            .AsNoTracking()
            .Where(p => deviceIdSet.Contains(p.DeviceId))
            .Select(p => new { p.DeviceId, p.ProcessId, p.Name, p.ExecutablePath })
            .ToListAsync(cancellationToken);

        var processesByDevice = processes
            .GroupBy(p => p.DeviceId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RunningProcess>)g
                    .Select(p => new RunningProcess(p.ProcessId, p.Name, p.ExecutablePath))
                    .ToList());

        var outcomes = new List<ForceStopDeviceOutcome>(devices.Count);
        var queuedTotal = 0;

        foreach (var device in devices)
        {
            // Publisher narrows the match only when both sides declare one:
            // inventory frequently omits it, and treating that as a mismatch
            // would make an installed application unstoppable.
            var installs = software
                .Where(s => s.DeviceId == device.Id)
                .Where(s => string.IsNullOrWhiteSpace(publisher)
                    || string.IsNullOrWhiteSpace(s.Publisher)
                    || string.Equals(s.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (installs.Count == 0)
            {
                outcomes.Add(new ForceStopDeviceOutcome(
                    device.Id, device.Hostname, ForceStopOutcome.NotInstalled, 0));
                continue;
            }

            var running = processesByDevice.TryGetValue(device.Id, out var found) ? found : [];

            var matched = installs
                .SelectMany(i => ApplicationProcessMatcher.Match(i.InstallLocation, running))
                .GroupBy(m => m.ProcessId)
                .Select(g => g.First())
                .ToList();

            if (matched.Count == 0)
            {
                // Two different situations, deliberately reported apart: the
                // application cannot be resolved to processes at all, or it can
                // but nothing of it is running. Only the first makes Force Stop
                // permanently unavailable.
                var resolvable = installs.Any(i => ApplicationProcessMatcher.CanResolve(i.InstallLocation));

                outcomes.Add(new ForceStopDeviceOutcome(
                    device.Id, device.Hostname,
                    resolvable ? ForceStopOutcome.NotRunning : ForceStopOutcome.Unresolvable,
                    0));
                continue;
            }

            var queued = 0;
            foreach (var process in matched)
            {
                // Queued through the task service so the retired-device rule, the
                // catalog definition and the minimum agent version all still
                // apply. The agent re-checks the image name against the live
                // process before terminating, so a pid that has been recycled
                // since inventory was collected is refused on the endpoint.
                var task = await _taskService.QueueAsync(
                    organizationId, device.Id, DeviceTaskType.TerminateProcess,
                    new TaskPayloads.TerminateProcess(process.ProcessId, process.ImageName),
                    actorId, actorDisplay, cancellationToken);

                if (task is not null)
                {
                    queued++;
                }
            }

            queuedTotal += queued;

            outcomes.Add(new ForceStopDeviceOutcome(
                device.Id, device.Hostname,
                // A device whose tasks were all refused is retired or below the
                // required agent version; saying "queued 0" would look like the
                // application simply was not running.
                queued > 0 ? ForceStopOutcome.Queued : ForceStopOutcome.NotEligible,
                queued));
        }

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.application.force_stop", AuditResult.Success,
            a => a.OnTarget("application", applicationName, applicationName)
                  .Requiring(Permissions.Task.Execute)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      application = applicationName,
                      devices = outcomes.Count,
                      processesQueued = queuedTotal,
                      // Hostnames and outcomes only. Command lines and full
                      // executable paths are deliberately not recorded.
                      results = outcomes.Select(o => new { o.Hostname, outcome = o.Outcome.ToString() }),
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new ForceStopResult(outcomes, queuedTotal);
    }
}

/// <summary>What happened for one device.</summary>
public enum ForceStopOutcome
{
    /// <summary>Termination tasks were queued.</summary>
    Queued = 0,

    /// <summary>The application is not installed on this device.</summary>
    NotInstalled = 1,

    /// <summary>Installed and resolvable, but nothing of it is running.</summary>
    NotRunning = 2,

    /// <summary>
    /// Installed, but no reliable mapping to processes exists -- Force Stop is
    /// not available for it.
    /// </summary>
    Unresolvable = 3,

    /// <summary>Retired, or an agent too old to run the task.</summary>
    NotEligible = 4,
}

public sealed record ForceStopDeviceOutcome(
    Guid DeviceId, string Hostname, ForceStopOutcome Outcome, int ProcessesQueued);

public sealed record ForceStopResult(
    IReadOnlyList<ForceStopDeviceOutcome> Devices, int ProcessesQueued);
