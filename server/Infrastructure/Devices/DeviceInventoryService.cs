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
