using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Software deployments: plan one, create it, and read what happened.
/// </summary>
/// <remarks>
/// Bulk by design. One request carries every device and group, and the server
/// resolves them — the alternative, a request per device, is 350 round trips for
/// one operator action and gives the fleet no single record of what was intended.
/// </remarks>
public static class DeploymentEndpoints
{
    /// <summary>Refuses an implausible target list before any query runs.</summary>
    private const int MaxTargetIds = 2000;

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/deployments");

        group.MapGet("/", ListAsync)
            .WithName("ListDeployments")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/{deploymentId:guid}", GetAsync)
            .WithName("GetDeployment")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/preview", PreviewAsync)
            .WithName("PreviewDeployment")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/", CreateAsync)
            .WithName("CreateDeployment")
            .RequirePermission(Permissions.Software.Deploy);

        return endpoints;
    }

    /// <summary>
    /// What deploying would do, without doing it.
    /// </summary>
    /// <remarks>
    /// Requires <c>software.deploy</c>, not merely view: the resolution discloses
    /// which devices have which software, and it is the preface to an action only
    /// a deployer may take.
    /// </remarks>
    private static async Task<IResult> PreviewAsync(
        DeploymentRequest request,
        SoftwareDeploymentService deploymentService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var plan = await deploymentService.PlanAsync(
            actor.OrganizationId, request.PackageId, request.DeviceIds ?? [], request.GroupIds ?? [],
            scopedDeviceIds, cancellationToken);

        // Missing, another organization's, or withdrawn — all 404, so a caller
        // cannot tell a package they may not use from one that does not exist.
        return plan is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                plan.PackageId,
                plan.PackageName,
                plan.PackageVersion,
                plan.Targeted,
                plan.NeedsInstall,
                plan.AlreadyInstalled,
                plan.NewerInstalled,
                plan.Retired,
                plan.NotComparable,
            });
    }

    private static async Task<IResult> CreateAsync(
        DeploymentRequest request,
        SoftwareDeploymentService deploymentService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await deploymentService.CreateAsync(
            actor.OrganizationId, request.PackageId, request.DeviceIds ?? [], request.GroupIds ?? [],
            scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        // Accepted, not Created: the tasks exist, the installs have not happened.
        // The agent does the work on its own schedule and the request must never
        // wait for it.
        return Results.Accepted(
            $"/admin/v1/deployments/{result.DeploymentId}",
            new { result.DeploymentId, result.Targeted, result.Queued, result.Skipped });
    }

    private static async Task<IResult> ListAsync(
        SoftwareDeploymentReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 25)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        return Results.Ok(await readService.ListAsync(
            actor.OrganizationId, scopedDeviceIds, page, pageSize, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid deploymentId,
        SoftwareDeploymentReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var detail = await readService.GetAsync(
            actor.OrganizationId, deploymentId, scopedDeviceIds, cancellationToken);

        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    /// <summary>Shape checks only; every id is authorized server-side afterwards.</summary>
    private static IResult? Validate(DeploymentRequest request)
    {
        if (request.PackageId == Guid.Empty)
        {
            return Results.Problem("packageId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var devices = request.DeviceIds?.Count ?? 0;
        var groups = request.GroupIds?.Count ?? 0;

        if (devices == 0 && groups == 0)
        {
            return Results.Problem(
                "Provide at least one deviceId or groupId.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (devices + groups > MaxTargetIds)
        {
            return Results.Problem(
                $"At most {MaxTargetIds} targets may be submitted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }
}

/// <summary>
/// A deployment request. Devices and groups may be combined; the union is
/// de-duplicated so a device in two targeted groups is deployed to once.
/// </summary>
public sealed record DeploymentRequest(
    Guid PackageId, IReadOnlyList<Guid>? DeviceIds, IReadOnlyList<Guid>? GroupIds);
