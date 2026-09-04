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
/// this platform's own inventory. Accepting an image name, an executable path or
/// a pid from the browser would be accepting an instruction to terminate an
/// arbitrary process, which is precisely the capability the typed-task
/// architecture exists to deny.
/// </para>
/// <para>
/// <b>No pid is chosen here.</b> This service decides only <em>whether</em> an
/// application can be stopped on a device and queues one
/// <see cref="DeviceTaskType.StopApplication"/> task carrying the install
/// directory. Which processes that resolves to is determined on the endpoint, at
/// the moment of termination, from live enumeration.
/// </para>
/// <para>
/// That split is the whole point. Inventory is collected on request, not
/// continuously: a process list measured on this fleet was ninety minutes old.
/// Naming a pid from it would name a process that has very likely exited,
/// restarted under a new pid, or had its pid reused -- and the endpoint's guard
/// would then correctly refuse, making Force Stop fail on an application that is
/// running perfectly well.
/// </para>
/// <para>
/// The install directory is server-derived from inventory and re-validated on the
/// endpoint; neither side trusts the other to have done it.
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

        // One query. The process list is deliberately NOT read here: it is a
        // snapshot from whenever inventory last ran -- ninety minutes old on this
        // fleet when measured -- and choosing a pid from it would be a guess about
        // a machine that has carried on since. The endpoint enumerates its own
        // processes at execution time instead.
        var software = await _dbContext.DeviceSoftware
            .AsNoTracking()
            .Where(s => deviceIdSet.Contains(s.DeviceId) && s.Name == applicationName)
            .Select(s => new { s.DeviceId, s.Name, s.Publisher, s.InstallLocation })
            .ToListAsync(cancellationToken);

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

            // The install directory this platform recorded. Whether any process is
            // actually running under it is not decided here -- that is a question
            // only the endpoint can answer without a stale gap.
            var location = installs
                .Select(i => i.InstallLocation)
                .FirstOrDefault(ApplicationInstallLocation.IsUsable);

            if (location is null)
            {
                // Nothing links this application to a process, and nothing will:
                // reported as permanently unavailable rather than as a failure the
                // operator would retry.
                outcomes.Add(new ForceStopDeviceOutcome(
                    device.Id, device.Hostname, ForceStopOutcome.Unresolvable, 0));
                continue;
            }

            // One task per device, not one per process. Which processes exist is
            // the endpoint's determination, made at the moment it acts.
            var task = await _taskService.QueueAsync(
                organizationId, device.Id, DeviceTaskType.StopApplication,
                new TaskPayloads.StopApplication(applicationName, publisher, location),
                actorId, actorDisplay, cancellationToken);

            if (task is null)
            {
                // Retired, or an agent without the executor. Distinguished from
                // "not running" because the operator can act on it -- by updating
                // the agent -- whereas "not running" needs nothing.
                outcomes.Add(new ForceStopDeviceOutcome(
                    device.Id, device.Hostname, ForceStopOutcome.NotEligible, 0));
                continue;
            }

            queuedTotal++;

            outcomes.Add(new ForceStopDeviceOutcome(
                device.Id, device.Hostname, ForceStopOutcome.Queued, 1));
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

    /// <summary>
    /// Reserved for the endpoint answer: installed and resolvable, but nothing of
    /// it is running. The server no longer decides this -- only the agent can,
    /// and it reports it as a task result.
    /// </summary>
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
