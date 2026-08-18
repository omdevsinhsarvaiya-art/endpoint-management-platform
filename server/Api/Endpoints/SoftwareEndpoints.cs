using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;

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

        group.MapGet("/publishers", PublishersAsync)
            .WithName("ListSoftwarePublishers")
            .RequirePermission(Permissions.Software.View);

        return endpoints;
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
