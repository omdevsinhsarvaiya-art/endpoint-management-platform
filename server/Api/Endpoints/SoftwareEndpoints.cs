using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Fleet-wide software inventory views (read-only, software.view).</summary>
public static class SoftwareEndpoints
{
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

        return endpoints;
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
