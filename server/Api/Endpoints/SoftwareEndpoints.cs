using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Fleet-wide software inventory views (read-only, software.view).</summary>
public static class SoftwareEndpoints
{
    /// <summary>Bounds an implausible request before any query runs.</summary>
    private const int MaxForceStopDevices = 500;

    public static IEndpointRouteBuilder MapSoftwareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/software");

        group.MapGet("/", ListAsync)
            .WithName("ListSoftwareTitles")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/installations", InstallationsAsync)
            .WithName("ListSoftwareInstallations")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/publishers", PublishersAsync)
            .WithName("ListSoftwarePublishers")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/force-stop", ForceStopAsync)
            .WithName("ForceStopApplication")
            .RequirePermission(Permissions.Task.Execute);

        return endpoints;
    }

    /// <summary>
    /// Stops a named installed application on one or more devices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <c>task.execute</c>, the same permission as terminating a process
    /// directly, because that is what this ultimately does.
    /// </para>
    /// <para>
    /// The body names an <em>application</em>. It cannot name a process, an image
    /// or a path: the server resolves those from its own inventory, so no request
    /// from a browser can ask the fleet to terminate something arbitrary.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ForceStopAsync(
        ForceStopRequest request,
        ApplicationForceStopService forceStopService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 384)
        {
            return Results.Problem("An application name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds is not { Count: > 0 })
        {
            return Results.Problem("At least one deviceId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds.Count > MaxForceStopDevices)
        {
            return Results.Problem(
                $"At most {MaxForceStopDevices} devices may be targeted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await forceStopService.StopAsync(
            actor.OrganizationId, request.DeviceIds, request.Name.Trim(), request.Publisher,
            scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        // Accepted, not Ok: the tasks exist, the processes have not been stopped
        // yet. The agent does that on its next poll.
        return Results.Accepted("/admin/v1/software", new
        {
            result.ProcessesQueued,
            devices = result.Devices.Select(d => new
            {
                d.DeviceId,
                d.Hostname,
                outcome = d.Outcome.ToString(),
                d.ProcessesQueued,
            }),
        });
    }

    /// <summary>
    /// Which devices have one title installed.
    /// </summary>
    /// <remarks>
    /// Device-scoped, because this names machines rather than counting them. An
    /// administrator restricted to a group sees only their devices; the response
    /// is narrowed rather than refused, so scope never reveals that a device it
    /// excludes exists.
    /// </remarks>
    private static async Task<IResult> InstallationsAsync(
        SoftwareReadService softwareReadService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        string name,
        string? version,
        string? publisher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.Problem("name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await softwareReadService.ListInstallationsAsync(
            actor.OrganizationId, scopedDeviceIds, name, version, publisher, page, pageSize, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ListAsync(
        SoftwareReadService softwareReadService,
        HttpContext httpContext,
        string? search,
        string? publisher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var result = await softwareReadService.ListTitlesAsync(
            organizationId, page, pageSize, search, publisher, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> PublishersAsync(
        SoftwareReadService softwareReadService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        return Results.Ok(await softwareReadService.ListPublishersAsync(organizationId, cancellationToken));
    }
}

/// <summary>
/// A Force Stop request: an application, and the devices to stop it on.
/// </summary>
/// <remarks>
/// Deliberately has no field for a process name, image or executable path. The
/// server resolves those from inventory, so the browser cannot ask for an
/// arbitrary process to be terminated.
/// </remarks>
public sealed record ForceStopRequest(
    IReadOnlyList<Guid>? DeviceIds, string? Name, string? Publisher);
