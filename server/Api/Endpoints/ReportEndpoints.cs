using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Reporting;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Consolidated fleet report (device.view).</summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/reports/summary", SummaryAsync)
            .WithName("GetFleetReport")
            .RequirePermission(Permissions.Device.View);
        return endpoints;
    }

    private static async Task<IResult> SummaryAsync(
        ReportReadService reportReadService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        return Results.Ok(await reportReadService.GetSummaryAsync(organizationId, cancellationToken));
    }
}
