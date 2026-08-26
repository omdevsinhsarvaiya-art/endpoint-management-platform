using System.Text.Json;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Ingests inventory uploads from authenticated agents.
/// </summary>
/// <remarks>
/// <para>
/// Input is agent-reported and therefore untrusted: lengths and ranges are
/// validated (the endpoint pre-validates shape, the domain re-validates on
/// construction), collection sizes are capped, and free-text values are stored
/// verbatim but never interpreted. A hostile agent can lie about its own facts —
/// that is inherent — but it must not be able to damage the platform or another
/// device's record.
/// </para>
/// <para>
/// The snapshot replaces the previous one atomically: hardware row upserted,
/// network interface rows deleted and re-inserted, device fields updated, all in
/// the one SaveChanges.
/// </para>
/// </remarks>
public sealed class DeviceInventoryService(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    AuditWriter auditWriter,
    ILogger<DeviceInventoryService> logger)
{
    /// <summary>Caps that no legitimate machine exceeds; anything above is a hostile payload.</summary>
    public const int MaxDisks = 64;
    public const int MaxNetworkInterfaces = 64;
    public const int MaxIpAddressesPerInterface = 32;
    public const int MaxLocalUsers = 2048;
    public const int MaxLocalGroups = 512;
    public const int MaxGroupMembers = 2048;
    public const int MaxSoftwareEntries = 8192;
    public const int MaxServices = 2048;
    public const int MaxProcesses = 500;
    public const int MaxUpdateHistory = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly ILogger<DeviceInventoryService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task ApplyAsync(Device device, InventoryReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(report);

        var now = _timeProvider.GetUtcNow();

        var hardware = await _dbContext.DeviceHardware
            .SingleOrDefaultAsync(h => h.DeviceId == device.Id, cancellationToken);

        if (hardware is null)
        {
            hardware = new DeviceHardware(device.Id);
            _dbContext.DeviceHardware.Add(hardware);
        }

        var disks = (report.Hardware.Disks ?? []).Take(MaxDisks)
            .Select(d => new
            {
                name = Truncate(d.Name, 64),
                fileSystem = Truncate(d.FileSystem, 32),
                sizeBytes = Math.Max(0, d.SizeBytes),
                freeBytes = Math.Max(0, d.FreeBytes),
            })
            .ToArray();

        hardware.Apply(
            report.Hardware.SerialNumber,
            report.Hardware.Manufacturer,
            report.Hardware.Model,
            report.Hardware.CpuName,
            report.Hardware.CpuPhysicalCores,
            report.Hardware.CpuLogicalProcessors,
            report.Hardware.TotalMemoryBytes,
            JsonSerializer.Serialize(disks, JsonOptions),
            now);

        // Replace the interface set wholesale.
        var existingInterfaces = await _dbContext.DeviceNetworkInterfaces
            .Where(n => n.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        _dbContext.DeviceNetworkInterfaces.RemoveRange(existingInterfaces);

        foreach (var reported in (report.NetworkInterfaces ?? []).Take(MaxNetworkInterfaces))
        {
            var addresses = (reported.IpAddresses ?? [])
                .Take(MaxIpAddressesPerInterface)
                .Select(ip => Truncate(ip, 64))
                .ToArray();

            _dbContext.DeviceNetworkInterfaces.Add(new DeviceNetworkInterface(
                device.Id,
                reported.Name,
                reported.MacAddress,
                JsonSerializer.Serialize(addresses, JsonOptions),
                reported.IsUp,
                now));
        }

        if (report.LocalAccounts is { } localAccounts)
        {
            await ApplyLocalAccountsAsync(device, localAccounts, now, cancellationToken);
        }

        if (report.Software is { } software)
        {
            await ApplySoftwareAsync(device, software, now, cancellationToken);
        }

        if (report.SecurityPosture is { } posture)
        {
            await ApplySecurityPostureAsync(device, posture, now, cancellationToken);
        }

        if (report.Services is { } || report.Processes is { })
        {
            await ApplyServicesProcessesAsync(device, report.Services, report.Processes, now, cancellationToken);
        }

        if (report.WindowsUpdate is { } windowsUpdate)
        {
            await ApplyWindowsUpdateAsync(device, windowsUpdate, now, cancellationToken);
        }

        device.RecordInventory(report.LoggedOnUser, now);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Inventory applied for device {DeviceId} ({Hostname}): {InterfaceCount} interface(s), "
            + "{DiskCount} disk(s).",
            device.Id,
            device.Hostname,
            Math.Min((report.NetworkInterfaces ?? []).Count, MaxNetworkInterfaces),
            disks.Length);
    }

    private static List<LocalAccountView> ToAccountViews(IEnumerable<DeviceLocalUser> users) =>
        users
            .Select(u => new LocalAccountView(u.Sid, u.Name, u.Enabled, u.IsLocalAdministrator))
            .ToList();

    /// <summary>
    /// Records a change in local-administrator posture, and only a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not an event per evaluation. The verdict is derived on
    /// read, so evaluating it is not a mutation, and an endpoint reporting its
    /// inventory every few minutes would otherwise write thousands of identical
    /// rows into an append-only store — burying the transitions that matter in
    /// the ones that do not.
    /// </para>
    /// <para>
    /// The transition is the security event: a machine that was standard-user
    /// and now has an interactive administrator is worth an alert, and so is the
    /// reverse, because somebody changed something. Attributed to the agent,
    /// because no operator performed this — the endpoint reported a new fact.
    /// </para>
    /// </remarks>
    private void AuditPostureTransition(
        Device device, LocalAdminPostureResult before, LocalAdminPostureResult after)
    {
        if (before.Compliance == after.Compliance)
        {
            return;
        }

        // Unknown -> anything on the first report is not a change in the
        // machine, only the arrival of evidence about it. Recording that as a
        // posture change would make every enrollment look like a security
        // transition.
        if (before.Compliance == LocalAdminCompliance.Unknown)
        {
            return;
        }

        _auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            device.Hostname,
            action: "localuser.posture.changed",
            after.Compliance == LocalAdminCompliance.NonCompliant
                ? AuditResult.Failure
                : AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device", device.Id.ToString(), device.Hostname)
                .WithStateChange(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        compliance = before.Compliance.ToString(),
                        interactiveAdministrators = before.InteractiveAdministrators
                            .Select(a => a.Username).ToList(),
                    }),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        compliance = after.Compliance.ToString(),
                        interactiveAdministrators = after.InteractiveAdministrators
                            .Select(a => a.Username).ToList(),
                    })));

        _logger.LogInformation(
            "Local administrator posture on {Hostname} changed from {Before} to {After}.",
            device.Hostname, before.Compliance, after.Compliance);
    }

    private async Task ApplyLocalAccountsAsync(
        Device device,
        InventoryLocalAccounts localAccounts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Replace-wholesale, same pattern as network interfaces.
        var existingUsers = await _dbContext.DeviceLocalUsers
            .Where(u => u.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        // The verdict this report replaces, computed before the rows go. Both
        // sets are already in hand here, which is the one place a change in
        // posture can be noticed without storing a cached verdict to compare
        // against.
        var postureBefore = LocalAdministratorPosture.Evaluate(ToAccountViews(existingUsers));

        _dbContext.DeviceLocalUsers.RemoveRange(existingUsers);

        var reportedUsers = new List<DeviceLocalUser>();

        foreach (var user in (localAccounts.Users ?? []).Take(MaxLocalUsers))
        {
            reportedUsers.Add(new DeviceLocalUser(
                device.Id,
                user.Sid,
                user.Name,
                user.FullName,
                user.Description,
                user.Enabled,
                user.PasswordRequired,
                user.PasswordExpires,
                user.LastLogon,
                user.IsLocalAdministrator,
                now));
        }

        _dbContext.DeviceLocalUsers.AddRange(reportedUsers);
        AuditPostureTransition(device, postureBefore, LocalAdministratorPosture.Evaluate(
            ToAccountViews(reportedUsers)));

        var existingGroups = await _dbContext.DeviceLocalGroups
            .Where(g => g.DeviceId == device.Id)
            .ToListAsync(cancellationToken);
        _dbContext.DeviceLocalGroups.RemoveRange(existingGroups);

        foreach (var group in (localAccounts.Groups ?? []).Take(MaxLocalGroups))
        {
            var members = (group.Members ?? []).Take(MaxGroupMembers)
                .Select(m => new
                {
                    name = Truncate(m.Name, 256),
                    sid = Truncate(m.Sid, 184),
                    memberType = Truncate(m.MemberType, 16),
                })
                .ToArray();

            _dbContext.DeviceLocalGroups.Add(new DeviceLocalGroup(
                device.Id,
                group.Sid,
                group.Name,
                group.Description,
                JsonSerializer.Serialize(members, JsonOptions),
                members.Length,
                now));
        }
    }

    private async Task ApplySoftwareAsync(
        Device device,
        IReadOnlyList<InventorySoftware> software,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.DeviceSoftware
            .Where(s => s.DeviceId == device.Id)
            .ToListAsync(cancellationToken);
        _dbContext.DeviceSoftware.RemoveRange(existing);

        foreach (var app in software.Take(MaxSoftwareEntries))
        {
            if (string.IsNullOrWhiteSpace(app.Name))
            {
                continue;
            }

            _dbContext.DeviceSoftware.Add(new DeviceSoftware(
                device.Id,
                Truncate(app.Name, 384)!,
                Truncate(app.Version, 128),
                Truncate(app.Publisher, 256),
                Truncate(app.InstallDate, 32),
                Truncate(app.InstallLocation, 512),
                Truncate(app.Architecture, 16),
                now));
        }
    }

    private async Task ApplySecurityPostureAsync(
        Device device,
        InventorySecurityPosture posture,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.DeviceSecurityPosture
            .SingleOrDefaultAsync(p => p.DeviceId == device.Id, cancellationToken);

        if (row is null)
        {
            row = new DeviceSecurityPosture(device.Id);
            _dbContext.DeviceSecurityPosture.Add(row);
        }

        row.Apply(
            posture.DefenderAntivirusEnabled, posture.DefenderRealtimeProtectionEnabled, posture.DefenderSignatureAgeDays,
            posture.FirewallDomainEnabled, posture.FirewallPrivateEnabled, posture.FirewallPublicEnabled,
            posture.SecureBootEnabled, posture.TpmPresent, posture.TpmEnabled, posture.TpmSpecVersion,
            posture.BitLockerSystemDriveStatus, posture.LocalAdministratorCount, now);
    }

    private async Task ApplyServicesProcessesAsync(
        Device device,
        IReadOnlyList<InventoryService>? services,
        IReadOnlyList<InventoryProcess>? processes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (services is not null)
        {
            var existing = await _dbContext.DeviceServices
                .Where(s => s.DeviceId == device.Id).ToListAsync(cancellationToken);
            _dbContext.DeviceServices.RemoveRange(existing);

            foreach (var svc in services.Take(MaxServices))
            {
                if (string.IsNullOrWhiteSpace(svc.Name) || string.IsNullOrWhiteSpace(svc.DisplayName))
                {
                    continue;
                }

                _dbContext.DeviceServices.Add(new DeviceServiceEntry(
                    device.Id, Truncate(svc.Name, 256)!, Truncate(svc.DisplayName, 384)!,
                    Truncate(svc.Status, 32) ?? "Unknown", Truncate(svc.StartMode, 32) ?? "Unknown", now));
            }
        }

        if (processes is not null)
        {
            var existing = await _dbContext.DeviceProcesses
                .Where(p => p.DeviceId == device.Id).ToListAsync(cancellationToken);
            _dbContext.DeviceProcesses.RemoveRange(existing);

            foreach (var proc in processes.Take(MaxProcesses))
            {
                if (string.IsNullOrWhiteSpace(proc.Name) || proc.ProcessId < 0)
                {
                    continue;
                }

                _dbContext.DeviceProcesses.Add(new DeviceProcessEntry(
                    device.Id, proc.ProcessId, Truncate(proc.Name, 256)!,
                    Math.Max(0, proc.WorkingSetBytes), Truncate(proc.ExecutablePath, 512), now));
            }
        }
    }

    private async Task ApplyWindowsUpdateAsync(
        Device device, InventoryWindowsUpdate update, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var history = (update.History ?? []).Take(MaxUpdateHistory).ToArray();
        var failedCount = history.Count(h => h.Result is "Failed" or "Aborted");

        var status = await _dbContext.DeviceUpdateStatus
            .SingleOrDefaultAsync(u => u.DeviceId == device.Id, cancellationToken);
        if (status is null)
        {
            status = new DeviceUpdateStatus(device.Id);
            _dbContext.DeviceUpdateStatus.Add(status);
        }

        status.Apply(update.RebootRequired, failedCount, now);

        var existing = await _dbContext.DeviceUpdateHistory
            .Where(h => h.DeviceId == device.Id).ToListAsync(cancellationToken);
        _dbContext.DeviceUpdateHistory.RemoveRange(existing);

        foreach (var h in history)
        {
            if (string.IsNullOrWhiteSpace(h.Title))
            {
                continue;
            }

            _dbContext.DeviceUpdateHistory.Add(new DeviceUpdateHistoryEntry(
                device.Id, Truncate(h.Title, 384)!, h.Date,
                Truncate(h.Operation, 32) ?? "Other", Truncate(h.Result, 32) ?? "Unknown", now));
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
