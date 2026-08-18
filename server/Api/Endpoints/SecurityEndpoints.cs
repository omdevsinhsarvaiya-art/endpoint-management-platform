using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Fleet security posture overview (device.view).</summary>
public static class SecurityEndpoints
{
    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/security/overview", OverviewAsync)
            .WithName("GetSecurityOverview")
            .RequirePermission(Permissions.Device.View);

        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(
        SecurityReadService securityReadService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        return Results.Ok(await securityReadService.GetOverviewAsync(organizationId, cancellationToken));
    }
}
