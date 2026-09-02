using System.Text.Json;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
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

        // Authenticated only — the real check is per task type inside the handler:
        // you may cancel exactly the tasks you are permitted to queue. A static
        // permission here would either invent a new one or let a role cancel
        // work it could never have created.
        group.MapPost("/{deviceId:guid}/tasks/{taskId:guid}/cancel", CancelTaskAsync)
            .WithName("CancelDeviceTask")
            .RequireAuthorization();

        group.MapPost("/{deviceId:guid}/actions/control-service", ControlServiceAsync)
            .WithName("ControlDeviceService")
            .RequirePermission(Domain.Authorization.Permissions.Task.Execute);

        group.MapPost("/{deviceId:guid}/actions/terminate-process", TerminateProcessAsync)
            .WithName("TerminateDeviceProcess")
            .RequirePermission(Domain.Authorization.Permissions.Task.Execute);

        group.MapPost("/{deviceId:guid}/offboard", OffboardAsync)
            .WithName("OffboardDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.Retire);

        group.MapPost("/{deviceId:guid}/reactivate", ReactivateAsync)
            .WithName("ReactivateDevice")
            .RequirePermission(Domain.Authorization.Permissions.Device.Retire);

        group.MapPatch("/{deviceId:guid}/display-name", SetDisplayNameAsync)
            .WithName("SetDeviceDisplayName")
            .RequirePermission(Domain.Authorization.Permissions.Device.Rename);

        return endpoints;
    }

    /// <summary>Retires a device: it stops being a manageable endpoint.</summary>
    /// <remarks>
    /// <para>
    /// Device scope is checked first and on its own, before the lifecycle service is
    /// asked anything. Retiring is the most consequential thing that can be done to a
    /// device short of deleting it -- it revokes every credential and takes the
    /// machine out of management -- so it must not be reachable by an administrator
    /// scoped to a different group merely because the device shares their
    /// organization.
    /// </para>
    /// <para>
    /// Answered 404 rather than 403, matching every other device-scoped route: a
    /// caller who may not act on a device is not told whether it exists.
    /// </para>
    /// </remarks>
    private static async Task<IResult> OffboardAsync(
        Guid deviceId, DeviceLifecycleService lifecycleService, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await lifecycleService.OffboardAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);
        return result == DeviceLifecycleResult.NotFound ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// Returns a retired device to service. Scoped identically to retiring it --
    /// undoing a retirement is as consequential as making one.
    /// </summary>
    private static async Task<IResult> ReactivateAsync(
        Guid deviceId, DeviceLifecycleService lifecycleService, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await lifecycleService.ReactivateAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);
        return result == DeviceLifecycleResult.NotFound ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// A null or blank <c>DisplayName</c> clears the label, which restores the
    /// agent-reported hostname as the device's shown name.
    /// </summary>
    public sealed record SetDisplayNameRequest(string? DisplayName);

    private static async Task<IResult> SetDisplayNameAsync(
        Guid deviceId,
        SetDisplayNameRequest request,
        DeviceLifecycleService lifecycleService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Bounded here as well as in the domain so an oversized label is a 400
        // rather than a 500 from a guard exception.
        if (request.DisplayName is { } proposed && proposed.Trim().Length > 128)
        {
            return Results.Problem(
                title: "Display name must be at most 128 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await lifecycleService.RenameAsync(
            actor.OrganizationId, deviceId, request.DisplayName, actor.UserId, actor.Email, cancellationToken);

        return result == DeviceLifecycleResult.NotFound ? Results.NotFound() : Results.NoContent();
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

    private static async Task<IResult> CancelTaskAsync(
        Guid deviceId,
        Guid taskId,
        HttpContext httpContext,
        EndpointPlatformDbContext dbContext,
        DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        // Read the type first so authorization happens before any state change.
        // Unknown task and unknown device both answer 404 — a caller who cannot
        // cancel a task learns nothing about whether it exists.
        var taskType = await dbContext.DeviceTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId && t.DeviceId == deviceId && t.OrganizationId == actor.OrganizationId)
            .Select(t => (DeviceTaskType?)t.Type)
            .SingleOrDefaultAsync(cancellationToken);

        if (taskType is null)
        {
            return Results.NotFound();
        }

        var definition = DeviceTaskCatalog.Require(taskType.Value);
        if (!httpContext.User.HasClaim(AdminAuthenticationHandler.PermissionClaimType, definition.RequiredPermission))
        {
            return Results.Forbid();
        }

        var result = await taskService.CancelAsync(
            actor.OrganizationId, deviceId, taskId, actor.UserId, actor.Email, cancellationToken);

        return result switch
        {
            TaskCancelResult.Success => Results.NoContent(),
            TaskCancelResult.NotFound => Results.NotFound(),
            // Already delivered or terminal: a stale view, not a fault.
            _ => Results.Problem(
                title: "The task can no longer be cancelled — it was already delivered to the agent or has finished.",
                statusCode: StatusCodes.Status409Conflict),
        };
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
        int pageSize = 50,
        string? status = null)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;

        Domain.Devices.DeviceStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            // Unknown filter = 400, not an empty page: silence would read as
            // "no such devices", which is a claim.
            if (!Enum.TryParse<Domain.Devices.DeviceStatus>(status, ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    title: $"Unknown device status '{status}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            statusFilter = parsed;
        }

        var result = await deviceReadService.ListAsync(
            organizationId,
            page,
            pageSize,
            search,
            statusFilter,
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
        Microsoft.Extensions.Options.IOptions<Infrastructure.Configuration.AgentServerOptions> agentServerOptions,
        TimeProvider timeProvider,
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

        var updateStatus = await dbContext.DeviceUpdateStatus
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.DeviceId == deviceId, cancellationToken);

        var updateHistory = await dbContext.DeviceUpdateHistory
            .AsNoTracking()
            .Where(h => h.DeviceId == deviceId)
            .OrderByDescending(h => h.Date)
            .Take(100)
            .Select(h => new { h.Title, h.Date, h.Operation, h.Result })
            .ToListAsync(cancellationToken);

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
            // Hostname is what Windows calls itself; DisplayName is what this
            // console calls it. Both are sent so the dashboard can lead with the
            // label and still show which physical machine it refers to.
            device.Hostname,
            device.DisplayName,
            // Same online definition as the device list. The dashboard needs it
            // here so that queueing an action against an offline machine can say
            // "queued, runs when the agent reconnects" instead of implying the
            // action is happening now.
            IsOnline = device.IsOnline(
                timeProvider.GetUtcNow(),
                TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds)),
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
            WindowsUpdate = updateStatus is null ? null : new
            {
                updateStatus.RebootRequired,
                updateStatus.FailedUpdateCount,
                updateStatus.CollectedAt,
                History = updateHistory,
            },
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
