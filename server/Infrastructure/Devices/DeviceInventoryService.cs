using System.Text.Json;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Devices;
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
    ILogger<DeviceInventoryService> logger)
{
    /// <summary>Caps that no legitimate machine exceeds; anything above is a hostile payload.</summary>
    public const int MaxDisks = 64;
    public const int MaxNetworkInterfaces = 64;
    public const int MaxIpAddressesPerInterface = 32;
    public const int MaxLocalUsers = 2048;
    public const int MaxLocalGroups = 512;
    public const int MaxGroupMembers = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

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
        _dbContext.DeviceLocalUsers.RemoveRange(existingUsers);

        foreach (var user in (localAccounts.Users ?? []).Take(MaxLocalUsers))
        {
            _dbContext.DeviceLocalUsers.Add(new DeviceLocalUser(
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
