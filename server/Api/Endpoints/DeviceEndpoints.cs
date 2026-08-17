using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Read-only device views for the dashboard: list, counts, detail.
/// </summary>
/// <remarks>
/// Same Phase 1 security note as <see cref="EnrollmentTokenEndpoints"/>: no
/// authentication until Phase 3; localhost only. These are reads — the mutating
/// device actions (restart, retire...) do not exist yet and will arrive after
/// RBAC does.
/// </remarks>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices");

        group.MapGet("/", ListAsync).WithName("ListDevices");
        group.MapGet("/counts", CountsAsync).WithName("GetDeviceCounts");
        group.MapGet("/{deviceId:guid}", GetAsync).WithName("GetDevice");
        group.MapPost("/{deviceId:guid}/refresh-inventory", RefreshInventoryAsync)
            .WithName("RequestDeviceInventoryRefresh");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        DeviceReadService deviceReadService,
        EndpointPlatformDbContext dbContext,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken)
    {
        var organizationId = await GetDefaultOrganizationIdAsync(dbContext, cancellationToken);
        if (organizationId is null)
        {
            return Results.Ok(new DevicePage([], 0, 1, 50));
        }

        var result = await deviceReadService.ListAsync(
            organizationId.Value,
            page == 0 ? 1 : page,
            pageSize == 0 ? 50 : pageSize,
            search,
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> CountsAsync(
        DeviceReadService deviceReadService,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var organizationId = await GetDefaultOrganizationIdAsync(dbContext, cancellationToken);
        if (organizationId is null)
        {
            return Results.Ok(new DeviceCounts(0, 0, 0, 0));
        }

        return Results.Ok(await deviceReadService.CountsAsync(organizationId.Value, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        var hardware = await dbContext.DeviceHardware
            .AsNoTracking()
            .SingleOrDefaultAsync(h => h.DeviceId == deviceId, cancellationToken);

        var networkInterfaces = await dbContext.DeviceNetworkInterfaces
            .AsNoTracking()
            .Where(n => n.DeviceId == deviceId)
            .OrderBy(n => n.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            device.Id,
            device.Hostname,
            device.OperatingSystem,
            device.AgentVersion,
            Status = device.Status.ToString(),
            device.LastSeenAt,
            device.EnrolledAt,
            device.MachineIdentifier,
            device.LoggedOnUser,
            device.InventoryCollectedAt,
            InventoryRefreshPending = device.IsInventoryRefreshPending,
            Hardware = hardware is null
                ? null
                : new
                {
                    hardware.SerialNumber,
                    hardware.Manufacturer,
                    hardware.Model,
                    hardware.CpuName,
                    hardware.CpuPhysicalCores,
                    hardware.CpuLogicalProcessors,
                    hardware.TotalMemoryBytes,
                    Disks = hardware.DisksJson is null
                        ? (JsonElement?)null
                        : JsonSerializer.Deserialize<JsonElement>(hardware.DisksJson),
                    hardware.CollectedAt,
                },
            NetworkInterfaces = networkInterfaces.Select(n => new
            {
                n.Name,
                n.MacAddress,
                IpAddresses = n.IpAddressesJson is null
                    ? null
                    : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(n.IpAddressesJson),
                n.IsUp,
            }),
        });
    }

    /// <summary>
    /// Marks the device for inventory refresh; the agent picks the request up on
    /// its next heartbeat. Pull-based — the server never connects to an agent.
    /// </summary>
    private static async Task<IResult> RefreshInventoryAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        AuditWriter auditWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        device.RequestInventoryRefresh(timeProvider.GetUtcNow());

        var (actorId, actorDisplay) = DevelopmentActor.Get();

        auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "device.refresh_inventory",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .Requiring(Domain.Authorization.Permissions.Device.RefreshInventory));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Accepted();
    }

    private static Task<Guid?> GetDefaultOrganizationIdAsync(
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .OrderBy(o => o.CreatedAt)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
