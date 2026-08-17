using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Enrollment;
using Microsoft.AspNetCore.Mvc;
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

        return endpoints;
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

        return Results.Ok(new HeartbeatResponse(
            now,
            agentServerOptions.Value.HeartbeatIntervalSeconds,
            InventoryRequested: device.IsInventoryRefreshPending));
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

        return null;
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
