using System.Text.Json;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Device views and the inventory-refresh action, guarded by permission policies.
/// </summary>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices");

        group.MapGet("/", ListAsync)
            .WithName("ListDevices")
            .RequirePermission(Domain.Authorization.Permissions.Device.View);

        group.MapGet("/counts", CountsAsync)
            .WithName("GetDeviceCounts")
            .RequirePermission(Domain.Authorization.Permissions.Device.View);

        group.MapGet("/{deviceId:guid}", GetAsync)
            .WithName("GetDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.View);

        group.MapPost("/{deviceId:guid}/refresh-inventory", RefreshInventoryAsync)
            .WithName("RequestDeviceInventoryRefresh")
            .RequirePermission(Domain.Authorization.Permissions.Device.RefreshInventory);

        group.MapPost("/{deviceId:guid}/actions/restart", (Guid deviceId, HttpContext ctx, DeviceTaskService svc, CancellationToken ct)
                => QueueActionAsync(deviceId, DeviceTaskType.RestartDevice,
                    new TaskPayloads.RestartOrShutdown(30, "Your IT administrator initiated a restart."), ctx, svc, ct))
            .WithName("RestartDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.Restart);

        group.MapPost("/{deviceId:guid}/actions/shutdown", (Guid deviceId, HttpContext ctx, DeviceTaskService svc, CancellationToken ct)
                => QueueActionAsync(deviceId, DeviceTaskType.ShutdownDevice,
                    new TaskPayloads.RestartOrShutdown(30, "Your IT administrator initiated a shutdown."), ctx, svc, ct))
            .WithName("ShutdownDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.Shutdown);

        group.MapPost("/{deviceId:guid}/actions/lock", (Guid deviceId, HttpContext ctx, DeviceTaskService svc, CancellationToken ct)
                => QueueActionAsync(deviceId, DeviceTaskType.LockDevice, null, ctx, svc, ct))
            .WithName("LockDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.Lock);

        group.MapPost("/{deviceId:guid}/actions/signout", (Guid deviceId, HttpContext ctx, DeviceTaskService svc, CancellationToken ct)
                => QueueActionAsync(deviceId, DeviceTaskType.SignOutUser, null, ctx, svc, ct))
            .WithName("SignOutUser")
            .RequirePermission(Domain.Authorization.Permissions.Device.SignOutUser);

        group.MapGet("/{deviceId:guid}/tasks", ListTasksAsync)
            .WithName("ListDeviceTasks")
            .RequirePermission(Domain.Authorization.Permissions.Task.View);

        group.MapPost("/{deviceId:guid}/actions/control-service", ControlServiceAsync)
            .WithName("ControlDeviceService")
            .RequirePermission(Domain.Authorization.Permissions.Task.Execute);

        group.MapPost("/{deviceId:guid}/actions/terminate-process", TerminateProcessAsync)
            .WithName("TerminateDeviceProcess")
            .RequirePermission(Domain.Authorization.Permissions.Task.Execute);

        return endpoints;
    }

    public sealed record ControlServiceRequest(string ServiceName, string Action);
    public sealed record TerminateProcessRequest(int ProcessId, string ExpectedImageName);

    private static async Task<IResult> ControlServiceAsync(
        Guid deviceId,
        ControlServiceRequest request,
        HttpContext httpContext,
        DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceName) || request.ServiceName.Length > 256
            || request.Action is not ("Start" or "Stop" or "Restart"))
        {
            return Results.Problem(title: "Invalid service-control request.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var action = Enum.Parse<TaskPayloads.ServiceAction>(request.Action);
        var task = await taskService.QueueAsync(
            actor.OrganizationId, deviceId, DeviceTaskType.ControlService,
            new TaskPayloads.ControlService(request.ServiceName, action),
            actor.UserId, actor.Email, cancellationToken);

        return task is null ? Results.NotFound() : Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id });
    }

    private static async Task<IResult> TerminateProcessAsync(
        Guid deviceId,
        TerminateProcessRequest request,
        HttpContext httpContext,
        DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        if (request.ProcessId <= 4 || string.IsNullOrWhiteSpace(request.ExpectedImageName)
            || request.ExpectedImageName.Length > 256)
        {
            return Results.Problem(title: "Invalid terminate-process request.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var task = await taskService.QueueAsync(
            actor.OrganizationId, deviceId, DeviceTaskType.TerminateProcess,
            new TaskPayloads.TerminateProcess(request.ProcessId, request.ExpectedImageName),
            actor.UserId, actor.Email, cancellationToken);

        return task is null ? Results.NotFound() : Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id });
    }

    private static async Task<IResult> QueueActionAsync(
        Guid deviceId,
        DeviceTaskType type,
        object? payload,
        HttpContext httpContext,
        DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var task = await taskService.QueueAsync(
            actor.OrganizationId, deviceId, type, payload, actor.UserId, actor.Email, cancellationToken);

        return task is null
            ? Results.NotFound()
            : Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id, status = task.Status.ToString() });
    }

    private static async Task<IResult> ListTasksAsync(
        Guid deviceId,
        HttpContext httpContext,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        var tasks = await dbContext.DeviceTasks
            .AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.OrganizationId == organizationId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(100)
            .Select(t => new
            {
                t.Id,
                Type = t.Type.ToString(),
                Status = t.Status.ToString(),
                t.CreatedByDisplay,
                t.CreatedAt,
                t.DeliveredAt,
                t.CompletedAt,
                t.ResultMessage,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(tasks);
    }

    private static async Task<IResult> ListAsync(
        DeviceReadService deviceReadService,
        HttpContext httpContext,
        string? search,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        var result = await deviceReadService.ListAsync(
            organizationId,
            page,
            pageSize,
            search,
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> CountsAsync(
        DeviceReadService deviceReadService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        return Results.Ok(await deviceReadService.CountsAsync(organizationId, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Organization scoping: an administrator only ever sees their own
        // organization's devices, even with a guessed id.
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        var device = await dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        var hardware = await dbContext.DeviceHardware
            .AsNoTracking()
            .SingleOrDefaultAsync(h => h.DeviceId == deviceId, cancellationToken);

        var networkInterfaces = await dbContext.DeviceNetworkInterfaces
            .AsNoTracking()
            .Where(n => n.DeviceId == deviceId)
            .OrderBy(n => n.Name)
            .ToListAsync(cancellationToken);

        var localUsers = await dbContext.DeviceLocalUsers
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .OrderBy(u => u.Name)
            .ToListAsync(cancellationToken);

        var localGroups = await dbContext.DeviceLocalGroups
            .AsNoTracking()
            .Where(g => g.DeviceId == deviceId)
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);

        var software = await dbContext.DeviceSoftware
            .AsNoTracking()
            .Where(sw => sw.DeviceId == deviceId)
            .OrderBy(sw => sw.Name)
            .Select(sw => new { sw.Name, sw.Version, sw.Publisher, sw.InstallDate, sw.Architecture })
            .ToListAsync(cancellationToken);

        var posture = await dbContext.DeviceSecurityPosture
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.DeviceId == deviceId, cancellationToken);

        var services = await dbContext.DeviceServices
            .AsNoTracking()
            .Where(sv => sv.DeviceId == deviceId)
            .OrderBy(sv => sv.DisplayName)
            .Select(sv => new { sv.Name, sv.DisplayName, sv.Status, sv.StartMode })
            .ToListAsync(cancellationToken);

        var processes = await dbContext.DeviceProcesses
            .AsNoTracking()
            .Where(pr => pr.DeviceId == deviceId)
            .OrderByDescending(pr => pr.WorkingSetBytes)
            .Select(pr => new { pr.ProcessId, pr.Name, pr.WorkingSetBytes, pr.ExecutablePath, pr.CollectedAt })
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            device.Id,
            device.Hostname,
            device.OperatingSystem,
            device.AgentVersion,
            Status = device.Status.ToString(),
            device.LastSeenAt,
            device.EnrolledAt,
            device.MachineIdentifier,
            device.LoggedOnUser,
            device.InventoryCollectedAt,
            InventoryRefreshPending = device.IsInventoryRefreshPending,
            Hardware = hardware is null
                ? null
                : new
                {
                    hardware.SerialNumber,
                    hardware.Manufacturer,
                    hardware.Model,
                    hardware.CpuName,
                    hardware.CpuPhysicalCores,
                    hardware.CpuLogicalProcessors,
                    hardware.TotalMemoryBytes,
                    Disks = hardware.DisksJson is null
                        ? (JsonElement?)null
                        : JsonSerializer.Deserialize<JsonElement>(hardware.DisksJson),
                    hardware.CollectedAt,
                },
            NetworkInterfaces = networkInterfaces.Select(n => new
            {
                n.Name,
                n.MacAddress,
                IpAddresses = n.IpAddressesJson is null
                    ? null
                    : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(n.IpAddressesJson),
                n.IsUp,
            }),
            LocalUsers = localUsers.Select(u => new
            {
                u.Sid,
                u.Name,
                u.FullName,
                u.Description,
                u.Enabled,
                u.PasswordRequired,
                u.PasswordExpires,
                u.LastLogon,
                u.IsLocalAdministrator,
            }),
            LocalGroups = localGroups.Select(g => new
            {
                g.Sid,
                g.Name,
                g.Description,
                g.MemberCount,
                IsAdministrators = g.IsAdministratorsGroup,
                Members = (JsonElement?)JsonSerializer.Deserialize<JsonElement>(g.MembersJson),
            }),
            Software = software,
            Services = services,
            Processes = processes,
            SecurityPosture = posture is null ? null : new
            {
                posture.DefenderAntivirusEnabled,
                posture.DefenderRealtimeProtectionEnabled,
                posture.DefenderSignatureAgeDays,
                posture.FirewallDomainEnabled,
                posture.FirewallPrivateEnabled,
                posture.FirewallPublicEnabled,
                posture.SecureBootEnabled,
                posture.TpmPresent,
                posture.TpmEnabled,
                posture.TpmSpecVersion,
                posture.BitLockerSystemDriveStatus,
                posture.LocalAdministratorCount,
                posture.CollectedAt,
                ComplianceScore = posture.ComplianceScore(),
            },
        });
    }

    /// <summary>
    /// Marks the device for inventory refresh; the agent picks the request up on
    /// its next heartbeat. Pull-based — the server never connects to an agent.
    /// </summary>
    private static async Task<IResult> RefreshInventoryAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        AuditWriter auditWriter,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var device = await dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId, cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        device.RequestInventoryRefresh(timeProvider.GetUtcNow());

        auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.PlatformUser,
            actor.UserId,
            actor.Email,
            action: "device.refresh_inventory",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .Requiring(Domain.Authorization.Permissions.Device.RefreshInventory));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Accepted();
    }
}
