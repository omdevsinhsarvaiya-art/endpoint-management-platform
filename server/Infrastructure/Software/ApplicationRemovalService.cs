using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// Removes a named installed application from one or more devices: the endpoint
/// stops it, then uninstalls it, in one task.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client names an application, never an identity.</b> The request
/// carries a display name, a publisher and optionally a version -- values that
/// already exist in this platform's inventory. The product code or package
/// full name the endpoint acts on is chosen here, from that inventory, so no
/// request from a browser can ask the fleet to uninstall something arbitrary.
/// The publisher and version narrow which inventory row is chosen and go no
/// further: what the endpoint is told is the chosen row's own values.
/// </para>
/// <para>
/// <b>An absent version means "any version of this application on the
/// device"</b>, not "the row that has no version". A version that is present is
/// an exact pin and matches that build alone. The console sends no version when
/// it is offering the application rather than one build of it, so the two sides
/// cannot read the same request two ways.
/// </para>
/// <para>
/// <b>Whether a row can be removed is a domain decision</b>
/// (<see cref="SoftwareRemovability"/>), made per row and reported per device
/// with its reason. Nothing is guessed at: a per-user MSI, a Windows component,
/// the agent itself and anything without a typed installer identity are all
/// reported as not removable rather than queued to fail.
/// </para>
/// <para>
/// <b>When several rows match, the choice is defined rather than incidental.</b>
/// A device can hold a machine-wide install and a per-user one, or two builds a
/// failed upgrade left behind. The candidates are ordered -- machine scope, then
/// newest version, then identity, then row id -- so that the product code queued
/// for a given device and inventory is the same one every time, and an operator
/// who retries gets the answer they got before rather than a different build.
/// </para>
/// <para>
/// One task per device, and one task type. Stop-then-uninstall is ordered
/// inside the executor rather than expressed as two tasks, because two tasks
/// resolve independently and the uninstall could run first.
/// </para>
/// </remarks>
public sealed class ApplicationRemovalService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;

    /// <summary>Stands in for a version that does not compare numerically.</summary>
    private static readonly Version NoVersion = new(0, 0);

    public async Task<RemoveApplicationResult> RemoveAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> deviceIds,
        string applicationName,
        string? publisher,
        string? version,
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
            return new RemoveApplicationResult([], 0);
        }

        var deviceIdSet = devices.Select(d => d.Id).ToHashSet();

        // The whole row, because removability reads identity, scope, category and
        // package names.
        var softwareQuery = _dbContext.DeviceSoftware
            .AsNoTracking()
            .Where(s => deviceIdSet.Contains(s.DeviceId) && s.Name == applicationName);

        // A version, when given, is an exact pin: the operator clicked a specific
        // row and must not have a different build removed by proxy. When it is
        // absent the request means "any version of this application on the
        // device", and exactly one row is still chosen -- the ordering below says
        // which. Absent is deliberately not read as "the row with no version":
        // the console omits the field when it is offering the application rather
        // than one build of it.
        if (!string.IsNullOrWhiteSpace(version))
        {
            var pinned = version.Trim();
            softwareQuery = softwareQuery.Where(s => s.Version == pinned);
        }

        var software = await softwareQuery.ToListAsync(cancellationToken);

        var outcomes = new List<RemoveApplicationDeviceOutcome>(devices.Count);
        var queuedTotal = 0;

        foreach (var device in devices)
        {
            // Publisher narrows the match only when both sides declare one:
            // inventory frequently omits it, and treating that as a mismatch
            // would make an installed application unremovable.
            var installs = software
                .Where(s => s.DeviceId == device.Id)
                .Where(s => string.IsNullOrWhiteSpace(publisher)
                    || string.IsNullOrWhiteSpace(s.Publisher)
                    || string.Equals(s.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (installs.Count == 0)
            {
                outcomes.Add(NotQueued(device.Id, device.Hostname, RemoveApplicationOutcome.NotInstalled));
                continue;
            }

            // A device can hold several rows for one application: a machine-wide
            // install and a per-user one, or two builds a failed upgrade left
            // behind. Which of them is uninstalled must never depend on the order
            // the database happened to return them in, so the candidates are put
            // in a defined order here, most specific first, before one is chosen
            // -- and the same order decides which reason is reported when none of
            // them can be acted on.
            var candidates = installs
                .Select(row => (Row: row, Decision: SoftwareRemovability.Evaluate(row)))

                // Machine scope first: it is the more specific match and the only
                // one the endpoint can act on at all.
                .OrderBy(c => IsMachineScope(c.Row) ? 0 : 1)

                // Then the newest version. Ranked before it is compared so the
                // comparison can be numeric: ordinal alone ranks "9.0" above
                // "10.0" and would uninstall the older build. Opaque versions
                // fall back to ordinal among themselves, and a row with no
                // version sorts last -- a row that says which build it is, is
                // the more specific match.
                .ThenBy(c => VersionRank(c.Row.Version))
                .ThenByDescending(c => ParsedVersion(c.Row.Version))
                .ThenByDescending(c => c.Row.Version ?? string.Empty, StringComparer.Ordinal)

                // Then the identity, then the row id. The last key is unique, so
                // the order is total: the same row wins every time this runs.
                .ThenBy(c => c.Row.ProductCode ?? c.Row.PackageFullName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(c => c.Row.Id)
                .ToList();

            var removable = candidates.FirstOrDefault(c => c.Decision.Removable);
            if (removable.Row is null)
            {
                outcomes.Add(NotQueued(
                    device.Id, device.Hostname, RemoveApplicationOutcome.NotRemovable,
                    candidates[0].Decision.Reason));
                continue;
            }

            var row = removable.Row;
            var method = removable.Decision.Method!.Value;

            // The stop step needs a directory it can match processes against. When
            // inventory has none the endpoint can use, none is sent: the uninstall
            // proceeds without the stop rather than the whole removal being refused
            // for a reason that has nothing to do with uninstalling.
            var location = ApplicationInstallLocation.IsUsable(row.InstallLocation)
                ? row.InstallLocation
                : null;

            var task = await _taskService.QueueAsync(
                organizationId, device.Id, DeviceTaskType.RemoveApplication,
                new TaskPayloads.RemoveApplication(
                    applicationName,
                    // The publisher inventory recorded for the row that was
                    // chosen, and nothing else. Falling back to the request's
                    // publisher would put a caller-supplied string of any length
                    // into device_tasks.payload_json and into the task.queue
                    // audit entry copied from it -- for a value that is only
                    // ever used to narrow the match, and that inventory itself
                    // bounds to 256 characters. Null here means inventory
                    // recorded no publisher, which is the truth.
                    row.Publisher,
                    location,
                    method.ToString(),
                    method == ApplicationRemovalMethod.WindowsInstaller ? row.ProductCode : null,
                    method == ApplicationRemovalMethod.Package ? row.PackageFullName : null),
                actorId, actorDisplay, cancellationToken);

            if (task is null)
            {
                // Retired, or an agent without the executor. Distinguished from
                // "not removable" because the operator can act on it -- by updating
                // the agent -- whereas "not removable" will not change.
                outcomes.Add(NotQueued(device.Id, device.Hostname, RemoveApplicationOutcome.NotEligible));
                continue;
            }

            queuedTotal++;

            outcomes.Add(new RemoveApplicationDeviceOutcome(
                device.Id, device.Hostname, RemoveApplicationOutcome.Queued, null, method, task.Id));
        }

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "software.application.remove", AuditResult.Success,
            a => a.OnTarget("application", applicationName, applicationName)
                  .Requiring(Permissions.Software.Deploy)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      application = applicationName,
                      devices = outcomes.Count,
                      devicesQueued = queuedTotal,
                      // Hostnames, outcomes and reasons only. Install directories
                      // and product identities are deliberately not recorded here;
                      // the per-task audit record carries the payload.
                      results = outcomes.Select(o => new
                      {
                          hostname = o.Hostname,
                          outcome = o.Outcome.ToString(),
                          reason = o.Reason?.ToString(),
                      }),
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new RemoveApplicationResult(outcomes, queuedTotal);
    }

    private static RemoveApplicationDeviceOutcome NotQueued(
        Guid deviceId, string hostname, RemoveApplicationOutcome outcome, NotRemovableReason? reason = null) =>
        new(deviceId, hostname, outcome, reason, null, null);

    /// <summary>Whether inventory recorded this row as installed for the whole machine.</summary>
    /// <remarks>
    /// The endpoint acts as SYSTEM, so machine scope is also the only scope it
    /// can uninstall at all -- which is why this leads the candidate ordering
    /// rather than merely breaking a tie.
    /// </remarks>
    private static bool IsMachineScope(DeviceSoftware row) =>
        string.Equals(row.InstallationScope, "Machine", StringComparison.OrdinalIgnoreCase);

    /// <summary>0 for a version that compares numerically, 1 for opaque text, 2 for none.</summary>
    /// <remarks>
    /// Sorting on this before comparing keeps the three kinds apart, so a
    /// numeric version and an opaque one never have to be ordered against each
    /// other. That is what makes the candidate ordering a genuine total order
    /// instead of a comparison whose answer depends on the order rows arrived
    /// in -- which is the whole point of ordering them.
    /// </remarks>
    private static int VersionRank(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? 2
            : Version.TryParse(version.Trim(), out _) ? 0 : 1;

    /// <summary>The row's version as a comparable value, or 0.0 when it is not numeric.</summary>
    /// <remarks>
    /// Rows that fall back to 0.0 have already been separated out by
    /// <see cref="VersionRank"/>, so they only ever tie with each other and are
    /// then ordered by their text.
    /// </remarks>
    private static Version ParsedVersion(string? version) =>
        Version.TryParse(version?.Trim(), out var parsed) ? parsed : NoVersion;
}

/// <summary>What happened for one device.</summary>
public enum RemoveApplicationOutcome
{
    /// <summary>A removal task was queued.</summary>
    Queued = 0,

    /// <summary>The application is not installed on this device.</summary>
    NotInstalled = 1,

    /// <summary>
    /// Installed, but nothing the platform can remove through a typed call. The
    /// reason says why; it will not change by retrying.
    /// </summary>
    NotRemovable = 2,

    /// <summary>Retired, or an agent too old to run the task.</summary>
    NotEligible = 3,
}

/// <param name="Reason">Why the row cannot be removed, when the outcome is NotRemovable.</param>
/// <param name="Method">How the endpoint will remove it, when the outcome is Queued.</param>
/// <param name="TaskId">The queued task, when the outcome is Queued.</param>
public sealed record RemoveApplicationDeviceOutcome(
    Guid DeviceId,
    string Hostname,
    RemoveApplicationOutcome Outcome,
    NotRemovableReason? Reason,
    ApplicationRemovalMethod? Method,
    Guid? TaskId);

public sealed record RemoveApplicationResult(
    IReadOnlyList<RemoveApplicationDeviceOutcome> Devices, int DevicesQueued);
