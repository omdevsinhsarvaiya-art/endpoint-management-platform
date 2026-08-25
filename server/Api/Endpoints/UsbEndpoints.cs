using System.ComponentModel.DataAnnotations;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Peripherals;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// USB and peripheral control for administrators.
/// </summary>
/// <remarks>
/// <para>
/// Reading is <see cref="Permissions.Usb.View"/>; anything that changes what an
/// endpoint will let a USB device do is <see cref="Permissions.Usb.Manage"/>,
/// which Auditor does not hold and Helpdesk does not hold. The split is the
/// point: seeing that a stick is plugged into a laptop is support information,
/// while opening a read path off that laptop is a security decision.
/// </para>
/// <para>
/// There is no endpoint here that an endpoint's own user can reach. The agent's
/// only USB-related route is the report it POSTs under its device credential,
/// and that route cannot create or widen a grant.
/// </para>
/// </remarks>
public static class UsbEndpoints
{
    public static IEndpointRouteBuilder MapUsbEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/devices/{deviceId:guid}/usb-devices", ListForDeviceAsync)
            .WithName("ListDeviceUsbDevices")
            .RequirePermission(Permissions.Usb.View);

        endpoints.MapGet("/admin/v1/usb-access-requests", ListRequestsAsync)
            .WithName("ListUsbAccessRequests")
            .RequirePermission(Permissions.Usb.View);

        endpoints.MapPost("/admin/v1/devices/{deviceId:guid}/usb-devices/{usbDeviceId:guid}/grant", GrantAsync)
            .WithName("GrantUsbAccess")
            .RequirePermission(Permissions.Usb.Manage);

        endpoints.MapPost("/admin/v1/usb-access-requests/{requestId:guid}/revoke", RevokeAsync)
            .WithName("RevokeUsbAccess")
            .RequirePermission(Permissions.Usb.Manage);

        // Re-push the endpoint's current policy without changing it. The repair
        // for a device showing Drifted or Pending, and deliberately incapable of
        // granting anything: it sends exactly what BuildPolicyAsync already says.
        endpoints.MapPost("/admin/v1/devices/{deviceId:guid}/usb-devices/reapply", ReapplyAsync)
            .WithName("ReapplyUsbPolicy")
            .RequirePermission(Permissions.Usb.Manage);

        return endpoints;
    }

    private static async Task<IResult> ListForDeviceAsync(
        Guid deviceId,
        HttpContext httpContext,
        UsbReadService readService,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var exists = await dbContext.Devices.AnyAsync(
            d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId, cancellationToken);

        if (!exists)
        {
            return Results.NotFound();
        }

        var devices = await readService.ListForDeviceAsync(
            actor.OrganizationId, deviceId, cancellationToken);

        return Results.Ok(devices);
    }

    private static async Task<IResult> ListRequestsAsync(
        HttpContext httpContext,
        UsbReadService readService,
        CancellationToken cancellationToken,
        bool liveOnly = false,
        int limit = 100)
    {
        var actor = AdminActor.Required(httpContext.User);

        var requests = await readService.ListRequestsAsync(
            actor.OrganizationId, liveOnly, limit, cancellationToken);

        return Results.Ok(requests);
    }

    private static async Task<IResult> GrantAsync(
        Guid deviceId,
        Guid usbDeviceId,
        GrantUsbAccessRequest request,
        HttpContext httpContext,
        UsbService usbService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validation, validateAllProperties: true))
        {
            return Results.ValidationProblem(
                validation.ToDictionary(
                    v => v.MemberNames.FirstOrDefault() ?? "request",
                    v => new[] { v.ErrorMessage ?? "Invalid." }));
        }

        var actor = AdminActor.Required(httpContext.User);

        var (outcome, granted) = await usbService.GrantReadOnlyAsync(
            actor.OrganizationId,
            deviceId,
            usbDeviceId,
            request.Justification,
            TimeSpan.FromMinutes(request.DurationMinutes),
            actor.UserId,
            actor.Email,
            cancellationToken);

        return outcome switch
        {
            UsbGrantOutcome.Success => Results.Ok(new
            {
                requestId = granted!.Id,
                expiresAt = granted.ExpiresAt,
                policy = nameof(UsbStoragePolicy.ReadOnly),
            }),

            UsbGrantOutcome.DeviceNotFound or UsbGrantOutcome.UsbDeviceNotFound => Results.NotFound(),

            UsbGrantOutcome.NotStorage => Results.Problem(
                "Access policy applies to USB storage only. Other peripherals are inventoried but never "
                + "restricted — disabling a keyboard or mouse would lock the user out of their own machine.",
                statusCode: StatusCodes.Status409Conflict),

            UsbGrantOutcome.AlreadyGranted => Results.Problem(
                "This device already has a live grant. Revoke it before issuing another, so that the "
                + "expiry time stays unambiguous.",
                statusCode: StatusCodes.Status409Conflict),

            UsbGrantOutcome.InvalidDuration => Results.Problem(
                $"A grant must last between {UsbAccessRequest.MinimumDuration.TotalMinutes:0} minutes "
                + $"and {UsbAccessRequest.MaximumDuration.TotalHours:0} hours.",
                statusCode: StatusCodes.Status400BadRequest),

            _ => Results.Problem("Unhandled grant outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> RevokeAsync(
        Guid requestId,
        RevokeUsbAccessRequest? request,
        HttpContext httpContext,
        UsbService usbService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var outcome = await usbService.RevokeAsync(
            actor.OrganizationId, requestId, request?.Note, actor.UserId, actor.Email, cancellationToken);

        return outcome switch
        {
            UsbRevokeOutcome.Success => Results.NoContent(),
            UsbRevokeOutcome.NotFound => Results.NotFound(),
            UsbRevokeOutcome.NotLive => Results.Problem(
                "That grant is not live — it has already expired, been revoked, or was never approved.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("Unhandled revoke outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> ReapplyAsync(
        Guid deviceId,
        HttpContext httpContext,
        UsbService usbService,
        EndpointPlatformDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(
            d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId, cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        var task = await usbService.QueuePolicyPushAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);

        return task is null
            ? Results.Problem(
                "Could not queue the policy for this device. A retired device cannot be sent tasks.",
                statusCode: StatusCodes.Status409Conflict)
            : Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id });
    }
}

/// <param name="DurationMinutes">
/// How long read-only access should last. Bounded here and again in the domain,
/// because a validation attribute is a convenience and the invariant belongs to
/// the entity.
/// </param>
/// <param name="Justification">
/// Why access is needed. Required: a grant nobody can explain later is not an
/// auditable one.
/// </param>
public sealed record GrantUsbAccessRequest(
    [property: Range(5, 1440)] int DurationMinutes,
    [property: Required, StringLength(1000, MinimumLength = 3)] string Justification);

public sealed record RevokeUsbAccessRequest([property: StringLength(1000)] string? Note);
