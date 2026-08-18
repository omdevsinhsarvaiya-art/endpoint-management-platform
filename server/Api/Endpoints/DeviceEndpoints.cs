using System.Text.Json;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Device views and the inventory-refresh action, guarded by permission policies.
/// </summary>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices");

        group.MapGet("/", ListAsync)
            .WithName("ListDevices")
            .RequirePermission(Domain.Authorization.Permissions.Device.View);

        group.MapGet("/counts", CountsAsync)
            .WithName("GetDeviceCounts")
            .RequirePermission(Domain.Authorization.Permissions.Device.View);

        group.MapGet("/{deviceId:guid}", GetAsync)
            .WithName("GetDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.View);

        group.MapPost("/{deviceId:guid}/refresh-inventory", RefreshInventoryAsync)
            .WithName("RequestDeviceInventoryRefresh")
            .RequirePermission(Domain.Authorization.Permissions.Device.RefreshInventory);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        DeviceReadService deviceReadService,
        HttpContext httpContext,
        string? search,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        var result = await deviceReadService.ListAsync(
            organizationId,
            page,
            pageSize,
            search,
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> CountsAsync(
        DeviceReadService deviceReadService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        return Results.Ok(await deviceReadService.CountsAsync(organizationId, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Organization scoping: an administrator only ever sees their own
        // organization's devices, even with a guessed id.
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        var device = await dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

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
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var device = await dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId, cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        device.RequestInventoryRefresh(timeProvider.GetUtcNow());

        auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.PlatformUser,
            actor.UserId,
            actor.Email,
            action: "device.refresh_inventory",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .Requiring(Domain.Authorization.Permissions.Device.RefreshInventory));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Accepted();
    }
}
