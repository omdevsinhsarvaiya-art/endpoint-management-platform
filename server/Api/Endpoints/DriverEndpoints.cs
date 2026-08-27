using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Device driver inventory and driver health.
/// </summary>
/// <remarks>
/// <para>
/// Read-only in this milestone. Both routes are behind <c>driver.view</c> and the
/// device scope check; neither changes anything on an endpoint, and there is
/// deliberately no route here that does.
/// </para>
/// <para>
/// Inventory and health are separate routes because they answer separate questions.
/// <c>/drivers</c> is "what is on this machine" -- a list an operator scrolls.
/// <c>/driver-health</c> is "is anything wrong with it" -- a verdict, computed from
/// the same rows, that a console can act on without downloading a few thousand
/// devices to count them itself.
/// </para>
/// </remarks>
public static class DriverEndpoints
{
    public static IEndpointRouteBuilder MapDriverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}");

        group.MapGet("/drivers", ListAsync)
            .WithName("ListDeviceDrivers")
            .RequirePermission(Permissions.Driver.View);

        group.MapGet("/driver-health", GetHealthAsync)
            .WithName("GetDeviceDriverHealth")
            .RequirePermission(Permissions.Driver.View);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        // Faults are what anyone opens this for; the full list is thousands of
        // rows that are almost all fine.
        bool problemsOnly = false)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var rows = await dbContext.DeviceDrivers
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .OrderBy(d => d.DeviceName)
            .ToListAsync(cancellationToken);

        var projected = rows
            .Select(d => new { Row = d, Verdict = DriverHealth.Classify(d.ProblemCode) })
            .Where(x => !problemsOnly || x.Verdict.CountsAsFault)
            .Select(x => new
            {
                instanceId = x.Row.InstanceId,
                deviceName = x.Row.DeviceName,
                deviceClass = x.Row.DeviceClass,
                manufacturer = x.Row.Manufacturer,
                driverProvider = x.Row.DriverProvider,
                driverVersion = x.Row.DriverVersion,
                driverDate = x.Row.DriverDate,
                infName = x.Row.InfName,

                // Both the raw code and the verdict. The code is what Windows said
                // and what an engineer will search for; the verdict is what this
                // platform makes of it, and the two are never conflated.
                problemCode = x.Verdict.ProblemCode,
                health = x.Verdict.State.ToString(),
                faultKind = x.Verdict.FaultKind.ToString(),
                problemDescription = x.Verdict.Description,

                isSigned = x.Row.IsSigned,
                collectedAt = x.Row.CollectedAt,
            })
            .ToList();

        return Results.Ok(projected);
    }

    private static async Task<IResult> GetHealthAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var device = await dbContext.Devices
            .AsNoTracking()
            .Where(d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId)
            .Select(d => new { d.Id, d.Hostname, d.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        var rows = await dbContext.DeviceDrivers
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .Select(d => new { d.InstanceId, d.DeviceName, d.DeviceClass, d.ProblemCode, d.CollectedAt })
            .ToListAsync(cancellationToken);

        var health = DriverHealthSummary.Evaluate(
            rows.Select(r => new DriverView(r.InstanceId, r.DeviceName, r.DeviceClass, r.ProblemCode)).ToList());

        return Results.Ok(new
        {
            deviceId = device.Id,
            hostname = device.Hostname,
            displayName = device.DisplayName,

            state = health.OverallState.ToString(),

            // Null when nothing has been reported, which is exactly the case
            // Unknown exists for: the absent timestamp is the evidence that the
            // verdict is absent too.
            lastReportedAt = rows.Count == 0
                ? (DateTimeOffset?)null
                : rows.Max(r => r.CollectedAt),

            driverFaultCount = health.DriverFaultCount,
            deviceFaultCount = health.DeviceFaultCount,
            indeterminateFaultCount = health.IndeterminateFaultCount,

            // Reported, never counted as faults. This platform disables devices
            // itself -- USB storage restriction is exactly that -- so a disabled
            // device is an intended state, and an operator seeing the count needs
            // to know it was set aside rather than missed.
            disabledCount = health.DisabledCount,
            unknownCount = health.UnknownCount,
            totalCount = health.TotalCount,

            faults = health.Faults
                .Select(f => new
                {
                    instanceId = f.InstanceId,
                    deviceName = f.DeviceName,
                    deviceClass = f.DeviceClass,
                    problemCode = f.Verdict.ProblemCode,
                    faultKind = f.Verdict.FaultKind.ToString(),
                    problemDescription = f.Verdict.Description,
                })
                .ToList(),

            limitation =
                "Driver health is read from the Windows PnP problem code reported at the last inventory. "
                + "A device that is working but running an outdated or superseded driver reports no problem "
                + "code and is not flagged here.",
        });
    }

    /// <summary>
    /// Deliberately 404, matching the elevation endpoints: an administrator who
    /// cannot reach a device should not learn that it exists.
    /// </summary>
    private static IResult OutOfScope() => Results.NotFound();
}
