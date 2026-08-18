using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Groups;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Device group management.</summary>
public static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/groups");

        group.MapGet("/", ListAsync).WithName("ListGroups").RequirePermission(Permissions.Group.View);
        group.MapPost("/", CreateAsync).WithName("CreateGroup").RequirePermission(Permissions.Group.Manage);
        group.MapGet("/{groupId:guid}/members", MembersAsync).WithName("GroupMembers").RequirePermission(Permissions.Group.View);
        group.MapPost("/{groupId:guid}/members", AddMemberAsync).WithName("AddGroupMember").RequirePermission(Permissions.Group.Manage);
        group.MapDelete("/{groupId:guid}/members/{deviceId:guid}", RemoveMemberAsync).WithName("RemoveGroupMember").RequirePermission(Permissions.Group.Manage);

        return endpoints;
    }

    public sealed record CreateGroupRequest(string Name, string Description);
    public sealed record AddMemberRequest(Guid DeviceId);

    private static async Task<IResult> ListAsync(
        EndpointPlatformDbContext dbContext, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var orgId = AdminActor.Required(httpContext.User).OrganizationId;
        var groups = await (
            from g in dbContext.DeviceGroups.AsNoTracking()
            where g.OrganizationId == orgId
            orderby g.Name
            select new
            {
                g.Id, g.Name, g.Description, Type = g.Type.ToString(),
                MemberCount = dbContext.DeviceGroupMemberships.Count(m => m.GroupId == g.Id),
            }).ToListAsync(cancellationToken);
        return Results.Ok(groups);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateGroupRequest request, HttpContext httpContext,
        DeviceGroupService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200
            || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 512)
        {
            return Results.Problem(title: "Invalid group name or description.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        try
        {
            var group = await service.CreateAsync(
                actor.OrganizationId, request.Name.Trim(), request.Description.Trim(), actor.UserId, actor.Email, cancellationToken);
            return Results.Created($"/admin/v1/groups/{group.Id}", new { group.Id });
        }
        catch (DbUpdateException)
        {
            return Results.Problem(title: "A group with that name already exists.", statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> MembersAsync(
        Guid groupId, EndpointPlatformDbContext dbContext, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var orgId = AdminActor.Required(httpContext.User).OrganizationId;
        var members = await (
            from m in dbContext.DeviceGroupMemberships.AsNoTracking()
            join d in dbContext.Devices.AsNoTracking() on m.DeviceId equals d.Id
            join g in dbContext.DeviceGroups.AsNoTracking() on m.GroupId equals g.Id
            where m.GroupId == groupId && g.OrganizationId == orgId
            orderby d.Hostname
            select new { d.Id, d.Hostname, Status = d.Status.ToString() }).ToListAsync(cancellationToken);
        return Results.Ok(members);
    }

    private static async Task<IResult> AddMemberAsync(
        Guid groupId, [FromBody] AddMemberRequest request, HttpContext httpContext,
        DeviceGroupService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var ok = await service.AddMemberAsync(actor.OrganizationId, groupId, request.DeviceId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid groupId, Guid deviceId, HttpContext httpContext,
        DeviceGroupService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var ok = await service.RemoveMemberAsync(actor.OrganizationId, groupId, deviceId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }
}
