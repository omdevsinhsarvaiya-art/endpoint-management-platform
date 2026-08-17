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
        });
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
