using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Enrollment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.AgentApi.Endpoints;

/// <summary>
/// The agent-facing endpoint surface: enrollment and heartbeat.
/// </summary>
/// <remarks>
/// <para>
/// Every handler here treats its caller as hostile until authenticated. Refusals
/// are uniform 401/403/400 responses with no distinguishing detail — the reasons
/// live in the audit trail and the server log, not on the wire, so a probing
/// caller learns nothing about which part of their guess was wrong.
/// </para>
/// <para>
/// Request size is bounded by the host's default Kestrel limits; field lengths
/// are validated here before anything touches the domain.
/// </para>
/// </remarks>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(AgentProtocol.RoutePrefix);

        group.MapPost(AgentProtocol.Routes.Enroll, EnrollAsync)
            .WithName("EnrollAgent")
            .AllowAnonymous(); // The enrollment token IS the credential here.

        group.MapPost(AgentProtocol.Routes.Heartbeat, HeartbeatAsync)
            .WithName("AgentHeartbeat");

        group.MapPost(AgentProtocol.Routes.Inventory, InventoryAsync)
            .WithName("AgentInventory");

        group.MapGet(AgentProtocol.Routes.Tasks, ClaimTasksAsync)
            .WithName("AgentClaimTasks");

        group.MapPost(AgentProtocol.Routes.Tasks + "/{taskId:guid}" + AgentProtocol.Routes.TaskResultSuffix,
                PostTaskResultAsync)
            .WithName("AgentPostTaskResult");

        group.MapGet(AgentProtocol.Routes.Policies, GetPoliciesAsync)
            .WithName("AgentGetPolicies");

        group.MapPost(AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix, PostComplianceAsync)
            .WithName("AgentPostCompliance");

        group.MapGet(
                AgentProtocol.Routes.Packages + "/{packageId:guid}" + AgentProtocol.Routes.PackageContentSuffix,
                GetPackageContentAsync)
            .WithName("AgentGetPackageContent");

        return endpoints;
    }

    private static async Task<IResult> GetPackageContentAsync(
        Guid packageId,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Software.SoftwarePackageService packageService,
        Infrastructure.Software.IPackageContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        // The package must belong to the device's organization and not be withdrawn.
        var package = await packageService.GetDeployableAsync(
            auth.Device!.OrganizationId, packageId, cancellationToken);
        if (package is null)
        {
            return Results.NotFound();
        }

        var stream = await contentStore.OpenReadAsync(package.Sha256, cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        // The agent re-hashes and re-verifies the signer; these headers are hints, not trust.
        return Results.File(stream, "application/octet-stream", package.FileName, enableRangeProcessing: false);
    }

    private static async Task<IResult> GetPoliciesAsync(
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Policies.PolicyService policyService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var effective = await policyService.GetEffectivePoliciesAsync(auth.Device!.Id, cancellationToken);
        var policies = effective.Select(e => new AgentPolicy(
            e.Policy.Id, e.Version.Id, e.Version.VersionNumber, e.Policy.Type.ToString(), e.Version.DesiredStateJson)).ToArray();

        return Results.Ok(new AgentPolicyListResponse(policies));
    }

    private static async Task<IResult> PostComplianceAsync(
        [FromBody] AgentPolicyComplianceReport report,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Policies.PolicyService policyService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        if (report.Results is { Count: > 1000 })
        {
            return Results.Problem(title: "Too many compliance results.", statusCode: StatusCodes.Status400BadRequest);
        }

        var items = new List<Infrastructure.Policies.ComplianceInput>();
        foreach (var r in report.Results ?? [])
        {
            if (!Enum.TryParse<Domain.Policies.PolicyComplianceState>(r.State, out var state))
            {
                return Results.Problem(title: "Invalid compliance state.", statusCode: StatusCodes.Status400BadRequest);
            }

            var deviations = (r.Deviations ?? []).Where(d => d.Length <= 512).Take(64).ToArray();
            items.Add(new Infrastructure.Policies.ComplianceInput(
                r.PolicyId, r.PolicyVersionId, r.VersionNumber, state, deviations));
        }

        await policyService.RecordComplianceAsync(auth.Device!.OrganizationId, auth.Device.Id, items, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ClaimTasksAsync(
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Tasks.DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var claimed = await taskService.ClaimForDeviceAsync(auth.Device!.Id, cancellationToken);

        var tasks = claimed
            .Select(t => new AgentTask(t.Id, t.Type.ToString(), t.PayloadJson))
            .ToArray();

        return Results.Ok(new AgentTaskListResponse(tasks));
    }

    private static async Task<IResult> PostTaskResultAsync(
        Guid taskId,
        [FromBody] AgentTaskResult result,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Tasks.DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        if (result.Message is { Length: > 1024 })
        {
            return Results.Problem(title: "Result message too long.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A device may only report results for its OWN tasks: the task id is scoped
        // to the authenticated device, so a stolen credential cannot forge outcomes
        // for another machine.
        var applied = await taskService.CompleteAsync(
            auth.Device!.Id, taskId, result.Succeeded, result.Message, result.ResultJson, cancellationToken);

        return applied ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> EnrollAsync(
        [FromBody] EnrollRequest request,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentEnrollmentService enrollmentService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var validationError = ValidateEnrollRequest(request);
        if (validationError is not null)
        {
            return Results.Problem(title: validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await enrollmentService.EnrollAsync(
            request.EnrollmentToken.Trim(),
            request.Hostname.Trim(),
            request.MachineIdentifier.Trim(),
            request.AgentVersion.Trim(),
            string.IsNullOrWhiteSpace(request.OperatingSystem) ? null : request.OperatingSystem.Trim(),
            cancellationToken);

        if (!outcome.Success)
        {
            // Deliberately indistinguishable for unknown/expired/revoked/exhausted.
            return Results.Problem(
                title: "Enrollment refused.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new EnrollResponse(
            outcome.DeviceId,
            outcome.CredentialKeyId!,
            outcome.CredentialSecret!,
            outcome.ReEnrolled));
    }

    private static async Task<IResult> HeartbeatAsync(
        [FromBody] HeartbeatRequest request,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Persistence.EndpointPlatformDbContext dbContext,
        Infrastructure.Policies.PolicyService policyService,
        IOptions<AgentServerOptions> agentServerOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Authenticate before validating the body: an unauthenticated caller gets
        // 401 regardless of what they sent, and learns nothing about our schema.
        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        var validationError = ValidateHeartbeatRequest(request);
        if (validationError is not null)
        {
            return Results.Problem(title: validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        var device = authentication.Device!;
        var now = timeProvider.GetUtcNow();

        device.RecordHeartbeat(
            request.Hostname.Trim(),
            request.AgentVersion.Trim(),
            string.IsNullOrWhiteSpace(request.OperatingSystem) ? null : request.OperatingSystem.Trim(),
            now);

        // Heartbeats are routine, high-volume signals - they update last_seen but
        // do not each produce an audit entry, which would bury real events under
        // one row per device per minute. Enrollment, task execution and mutations
        // are the audited operations.
        await dbContext.SaveChangesAsync(cancellationToken);

        var tasksPending = await dbContext.DeviceTasks
            .AnyAsync(t => t.DeviceId == device.Id
                           && t.Status == Domain.Tasks.DeviceTaskStatus.Queued, cancellationToken);

        var policiesPending = await policyService.HasPendingComplianceAsync(device.Id, cancellationToken);

        return Results.Ok(new HeartbeatResponse(
            now,
            agentServerOptions.Value.HeartbeatIntervalSeconds,
            InventoryRequested: device.IsInventoryRefreshPending,
            TasksPending: tasksPending,
            PoliciesPending: policiesPending));
    }

    private static async Task<IResult> InventoryAsync(
        [FromBody] InventoryReport report,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Devices.DeviceInventoryService inventoryService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        var validationError = ValidateInventoryReport(report);
        if (validationError is not null)
        {
            return Results.Problem(title: validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        await inventoryService.ApplyAsync(authentication.Device!, report, cancellationToken);

        return Results.Ok(new InventoryResponse(timeProvider.GetUtcNow()));
    }

    private static string? ValidateInventoryReport(InventoryReport report)
    {
        if (report.Hardware is null)
        {
            return "Hardware section is required.";
        }

        if (report.NetworkInterfaces is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxNetworkInterfaces })
        {
            return "Too many network interfaces.";
        }

        if (report.Hardware.Disks is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxDisks })
        {
            return "Too many disks.";
        }

        foreach (var nic in report.NetworkInterfaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(nic.Name) || nic.Name.Length > 256)
            {
                return "Every network interface requires a name of at most 256 characters.";
            }

            if (nic.IpAddresses is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxIpAddressesPerInterface })
            {
                return "Too many IP addresses on one interface.";
            }

            // Pre-validate the MAC so a malformed one is a clean 400 here rather
            // than an exception inside the domain's normaliser.
            if (nic.MacAddress is { } mac)
            {
                var hexDigits = mac.Count(Uri.IsHexDigit);
                if (mac.Length > 23 || (hexDigits != 12 && hexDigits != 16))
                {
                    return "A network interface MAC address is malformed.";
                }
            }

            foreach (var ip in nic.IpAddresses ?? [])
            {
                if (string.IsNullOrWhiteSpace(ip) || ip.Length > 64)
                {
                    return "A network interface IP address is malformed.";
                }
            }
        }

        if (report.LoggedOnUser is { Length: > 256 })
        {
            return "Logged-on user must be at most 256 characters.";
        }

        if (report.Software is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxSoftwareEntries })
        {
            return "Too many software entries.";
        }

        foreach (var app in report.Software ?? [])
        {
            if (string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 384
                || app.Version is { Length: > 128 } || app.Publisher is { Length: > 256 }
                || app.InstallLocation is { Length: > 512 })
            {
                return "A software entry is malformed.";
            }
        }

        if (report.SecurityPosture is { } posture)
        {
            // Bounds guard against a hostile agent sending absurd values.
            if (posture.DefenderSignatureAgeDays is < 0 or > 3650
                || posture.LocalAdministratorCount is < 0 or > 100000
                || posture.TpmSpecVersion is { Length: > 32 }
                || posture.BitLockerSystemDriveStatus is { Length: > 32 })
            {
                return "Security posture values are out of range.";
            }
        }

        if (report.Services is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxServices })
        {
            return "Too many services.";
        }

        if (report.Processes is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxProcesses })
        {
            return "Too many processes.";
        }

        if (report.WindowsUpdate?.History is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxUpdateHistory })
        {
            return "Too many update history entries.";
        }

        if (report.LocalAccounts is { } accounts)
        {
            if (accounts.Users is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxLocalUsers })
            {
                return "Too many local users.";
            }

            if (accounts.Groups is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxLocalGroups })
            {
                return "Too many local groups.";
            }

            foreach (var user in accounts.Users ?? [])
            {
                if (!IsValidSid(user.Sid) || string.IsNullOrWhiteSpace(user.Name) || user.Name.Length > 256
                    || user.FullName is { Length: > 256 } || user.Description is { Length: > 512 })
                {
                    return "A local user entry is malformed.";
                }
            }

            foreach (var group in accounts.Groups ?? [])
            {
                if (!IsValidSid(group.Sid) || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 256
                    || group.Description is { Length: > 512 })
                {
                    return "A local group entry is malformed.";
                }

                if (group.Members is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxGroupMembers })
                {
                    return "A local group reports too many members.";
                }

                foreach (var member in group.Members ?? [])
                {
                    if (string.IsNullOrWhiteSpace(member.Name) || member.Name.Length > 256
                        || (member.Sid is { } sid && !IsValidSid(sid)))
                    {
                        return "A group member entry is malformed.";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Structural SID check: "S-1-..." with digits and dashes, bounded length.</summary>
    private static bool IsValidSid(string? sid)
    {
        if (sid is null || sid.Length is < 4 or > 184 || !sid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in sid.AsSpan(2))
        {
            if (c is not ((>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ValidateEnrollRequest(EnrollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || request.EnrollmentToken.Length > 128)
        {
            return "Enrollment token is missing or malformed.";
        }

        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 253)
        {
            return "Hostname is required and must be at most 253 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.MachineIdentifier) || request.MachineIdentifier.Length > 128)
        {
            return "Machine identifier is required and must be at most 128 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 64)
        {
            return "Agent version is required and must be at most 64 characters.";
        }

        if (request.OperatingSystem is { Length: > 256 })
        {
            return "Operating system must be at most 256 characters.";
        }

        return null;
    }

    private static string? ValidateHeartbeatRequest(HeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 253)
        {
            return "Hostname is required and must be at most 253 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 64)
        {
            return "Agent version is required and must be at most 64 characters.";
        }

        if (request.OperatingSystem is { Length: > 256 })
        {
            return "Operating system must be at most 256 characters.";
        }

        return null;
    }
}
