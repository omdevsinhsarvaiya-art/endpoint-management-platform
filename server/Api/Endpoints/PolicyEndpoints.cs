using System.Text.Json;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Policies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Policy management: create, version, assign, view compliance.</summary>
public static class PolicyEndpoints
{
    public static IEndpointRouteBuilder MapPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/policies");

        group.MapGet("/", ListAsync).WithName("ListPolicies").RequirePermission(Permissions.Policy.View);
        group.MapPost("/", CreateAsync).WithName("CreatePolicy").RequirePermission(Permissions.Policy.Create);
        group.MapPost("/{policyId:guid}/assign", AssignAsync).WithName("AssignPolicy").RequirePermission(Permissions.Policy.Assign);
        group.MapPost("/{policyId:guid}/assign-group", AssignGroupAsync).WithName("AssignPolicyToGroup").RequirePermission(Permissions.Policy.Assign);
        group.MapGet("/{policyId:guid}/compliance", ComplianceAsync).WithName("PolicyCompliance").RequirePermission(Permissions.Policy.View);

        return endpoints;
    }

    public sealed record CreatePolicyRequest(string Type, string Name, string Description, int MaxTimeoutSeconds);
    public sealed record AssignRequest(Guid DeviceId);

    private static async Task<IResult> ListAsync(
        EndpointPlatformDbContext dbContext, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var orgId = AdminActor.Required(httpContext.User).OrganizationId;

        var policies = await dbContext.Policies.AsNoTracking()
            .Where(p => p.OrganizationId == orgId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, Type = p.Type.ToString(), p.Name, p.Description, p.IsEnabled, p.CurrentVersionNumber })
            .ToListAsync(cancellationToken);

        // Attach compliance rollups.
        var rollups = await dbContext.PolicyComplianceResults.AsNoTracking()
            .Where(r => r.OrganizationId == orgId)
            .GroupBy(r => new { r.PolicyId, r.State })
            .Select(g => new { g.Key.PolicyId, g.Key.State, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var result = policies.Select(p => new
        {
            p.Id, p.Type, p.Name, p.Description, p.IsEnabled, p.CurrentVersionNumber,
            Compliant = rollups.Where(r => r.PolicyId == p.Id && r.State == PolicyComplianceState.Compliant).Sum(r => r.Count),
            NonCompliant = rollups.Where(r => r.PolicyId == p.Id && r.State == PolicyComplianceState.NonCompliant).Sum(r => r.Count),
            Unknown = rollups.Where(r => r.PolicyId == p.Id && r.State == PolicyComplianceState.Unknown).Sum(r => r.Count),
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreatePolicyRequest request, HttpContext httpContext,
        PolicyService policyService, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PolicyType>(request.Type, out var type))
        {
            return Results.Problem(title: "Unknown policy type.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200
            || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 512)
        {
            return Results.Problem(title: "Invalid policy name or description.", statusCode: StatusCodes.Status400BadRequest);
        }

        // v1: only ScreenLockTimeout, whose desired state is a max timeout.
        if (type != PolicyType.ScreenLockTimeout || request.MaxTimeoutSeconds is < 30 or > 86400)
        {
            return Results.Problem(title: "Invalid desired state for this policy type.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var desiredState = JsonSerializer.Serialize(new { maxTimeoutSeconds = request.MaxTimeoutSeconds });

        var policy = await policyService.CreateAsync(
            actor.OrganizationId, type, request.Name.Trim(), request.Description.Trim(), desiredState,
            actor.UserId, actor.Email, cancellationToken);

        return Results.Created($"/admin/v1/policies/{policy.Id}", new { policy.Id });
    }

    private static async Task<IResult> AssignAsync(
        Guid policyId, [FromBody] AssignRequest request, HttpContext httpContext,
        PolicyService policyService, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var ok = await policyService.AssignToDeviceAsync(
            actor.OrganizationId, policyId, request.DeviceId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    public sealed record AssignGroupRequest(Guid GroupId);

    private static async Task<IResult> AssignGroupAsync(
        Guid policyId, [FromBody] AssignGroupRequest request, HttpContext httpContext,
        EndpointPlatformDbContext dbContext, PolicyService policyService, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var policy = await dbContext.Policies
            .SingleOrDefaultAsync(p => p.Id == policyId && p.OrganizationId == actor.OrganizationId, cancellationToken);
        var groupExists = await dbContext.DeviceGroups
            .AnyAsync(g => g.Id == request.GroupId && g.OrganizationId == actor.OrganizationId, cancellationToken);
        if (policy is null || !groupExists)
        {
            return Results.NotFound();
        }

        var already = await dbContext.PolicyAssignments.AnyAsync(
            x => x.PolicyId == policyId && x.TargetType == PolicyAssignmentTarget.Group && x.TargetId == request.GroupId,
            cancellationToken);
        if (!already)
        {
            dbContext.PolicyAssignments.Add(new PolicyAssignment(
                actor.OrganizationId, policyId, PolicyAssignmentTarget.Group, request.GroupId));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ComplianceAsync(
        Guid policyId, EndpointPlatformDbContext dbContext, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var orgId = AdminActor.Required(httpContext.User).OrganizationId;

        var rows = await (
            from r in dbContext.PolicyComplianceResults.AsNoTracking()
            join d in dbContext.Devices.AsNoTracking() on r.DeviceId equals d.Id
            where r.PolicyId == policyId && r.OrganizationId == orgId
            orderby d.Hostname
            select new
            {
                r.DeviceId, d.Hostname, State = r.State.ToString(), r.PolicyVersionNumber, r.EvaluatedAt,
                Deviations = r.DeviationsJson,
            }).ToListAsync(cancellationToken);

        return Results.Ok(rows.Select(x => new
        {
            x.DeviceId, x.Hostname, x.State, x.PolicyVersionNumber, x.EvaluatedAt,
            Deviations = x.Deviations is null ? null : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(x.Deviations),
        }));
    }
}
