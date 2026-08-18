using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Fleet Windows Update overview (device.view).</summary>
public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/updates/overview", OverviewAsync)
            .WithName("GetUpdateOverview")
            .RequirePermission(Permissions.Device.View);
        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(
        UpdateReadService updateReadService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        return Results.Ok(await updateReadService.GetOverviewAsync(organizationId, cancellationToken));
    }
}
