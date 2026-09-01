using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Agents;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Agent release management: upload, publish, revoke, list, download — and
/// queueing a device's self-update to a published release.
/// </summary>
/// <remarks>
/// RBAC reuses the software permissions deliberately: publishing an agent build
/// that every endpoint may install IS deploying software fleet-wide, so the
/// roles trusted with <see cref="Permissions.Software.Deploy"/> are exactly the
/// roles trusted here, and viewing/downloading follows
/// <see cref="Permissions.Software.View"/>. The MSI itself is a universal,
/// secret-free binary — Auditor being able to download it discloses nothing.
/// </remarks>
public static class AgentReleaseEndpoints
{
    /// <summary>MSI ceiling. The current agent MSI is ~40 MB; 250 MB leaves headroom without inviting abuse.</summary>
    private const long MaxMsiBytes = 250L * 1024 * 1024;

    public static IEndpointRouteBuilder MapAgentReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/agent-releases");

        group.MapGet("/", ListAsync)
            .WithName("ListAgentReleases")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/latest", LatestAsync)
            .WithName("GetLatestAgentRelease")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/{releaseId:guid}/download", DownloadAsync)
            .WithName("DownloadAgentRelease")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/", CreateAsync)
            .WithName("CreateAgentRelease")
            .RequirePermission(Permissions.Software.Deploy)
            .DisableAntiforgery(); // multipart upload; CSRF is covered by the X-Requested-With gate.

        group.MapPost("/{releaseId:guid}/publish", (Guid releaseId, HttpContext ctx, AgentReleaseService svc, CancellationToken ct)
                => TransitionAsync(releaseId, ctx, svc, publish: true, ct))
            .WithName("PublishAgentRelease")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{releaseId:guid}/revoke", (Guid releaseId, HttpContext ctx, AgentReleaseService svc, CancellationToken ct)
                => TransitionAsync(releaseId, ctx, svc, publish: false, ct))
            .WithName("RevokeAgentRelease")
            .RequirePermission(Permissions.Software.Deploy);

        endpoints.MapPost("/admin/v1/devices/{deviceId:guid}/actions/update-agent", QueueUpdateAsync)
            .WithName("UpdateDeviceAgent")
            .RequirePermission(Permissions.Software.Deploy);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        EndpointPlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var releases = await dbContext.AgentReleases
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.Version,
                r.Platform,
                r.Architecture,
                r.FileName,
                r.Sha256,
                r.SignerSubject,
                r.ReleaseNotes,
                r.ContentSizeBytes,
                Status = r.Status.ToString(),
                r.CreatedByDisplay,
                r.CreatedAt,
                r.PublishedAt,
                r.RevokedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(releases);
    }

    private static async Task<IResult> LatestAsync(
        AgentReleaseService releaseService, CancellationToken cancellationToken)
    {
        var latest = await releaseService.GetLatestPublishedAsync(cancellationToken);
        return latest is null
            ? Results.Ok(new { available = false })
            : Results.Ok(new
            {
                available = true,
                releaseId = latest.Id,
                version = latest.Version,
                platform = latest.Platform,
                architecture = latest.Architecture,
                fileName = latest.FileName,
                sha256 = latest.Sha256,
                signerSubject = latest.SignerSubject,
                releaseNotes = latest.ReleaseNotes,
                sizeBytes = latest.ContentSizeBytes,
                publishedAt = latest.PublishedAt,
            });
    }

    private static async Task<IResult> DownloadAsync(
        Guid releaseId,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        AuditWriter auditWriter,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var (content, release) = await releaseService.OpenPublishedContentAsync(releaseId, cancellationToken);
        if (content is null || release is null)
        {
            return Results.NotFound();
        }

        // Who fetched which build matters when a bad MSI is being traced.
        var actor = AdminActor.Required(httpContext.User);
        auditWriter.Stage(
            actor.OrganizationId, AuditActorType.PlatformUser, actor.UserId, actor.Email,
            action: "agent_release.downloaded", AuditResult.Success,
            audit => audit.OnTarget("agent_release", release.Id.ToString(), $"windows/x64 {release.Version}"));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Stream(content, "application/octet-stream", release.FileName);
    }

    private static async Task<IResult> CreateAsync(
        AgentReleaseService releaseService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // Before the form is read: Kestrel's default 30 MB cap otherwise vetoes
        // the upload before the MaxMsiBytes check below ever runs.
        RequestBodyLimits.AllowUploadOf(httpContext, MaxMsiBytes);

        if (!httpContext.Request.HasFormContentType)
        {
            return Results.Problem("Expected a multipart/form-data upload.", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.Problem("A non-empty 'file' part is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxMsiBytes)
        {
            return Results.Problem("MSI exceeds the maximum allowed size.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? Field(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

        var version = Field("version");
        var sha256 = Field("sha256");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(sha256))
        {
            return Results.Problem("version and sha256 are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        await using var content = file.OpenReadStream();

        var (release, error) = await releaseService.CreateAsync(
            version!, file.FileName, sha256!, Field("signerSubject"), Field("releaseNotes"),
            content, actor.UserId, actor.Email, actor.OrganizationId, cancellationToken);

        return release is null
            ? Results.Problem(error ?? "The release could not be created.", statusCode: StatusCodes.Status400BadRequest)
            : Results.Created($"/admin/v1/agent-releases/{release.Id}", new { releaseId = release.Id, release.Version });
    }

    private static async Task<IResult> TransitionAsync(
        Guid releaseId, HttpContext httpContext, AgentReleaseService releaseService, bool publish,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = publish
            ? await releaseService.PublishAsync(releaseId, actor.UserId, actor.Email, actor.OrganizationId, cancellationToken)
            : await releaseService.RevokeAsync(releaseId, actor.UserId, actor.Email, actor.OrganizationId, cancellationToken);

        return result switch
        {
            AgentReleaseActionResult.Success => Results.NoContent(),
            AgentReleaseActionResult.NotFound => Results.NotFound(),
            _ => Results.Problem(
                "The release is not in a state that allows this action.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    public sealed record UpdateAgentRequest(Guid ReleaseId);

    /// <summary>
    /// Queues a device's self-update to a published release. The server refuses
    /// downgrades and same-version updates here, and the agent independently
    /// enforces the same rule — neither side trusts the other to have checked.
    /// </summary>
    private static async Task<IResult> QueueUpdateAsync(
        Guid deviceId,
        UpdateAgentRequest request,
        HttpContext httpContext,
        AgentReleaseService releaseService,
        EndpointPlatformDbContext dbContext,
        DeviceTaskService taskService,
        DeviceScopeAuthorizer scope,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        // Device scope, checked first and on its own.
        //
        // This route previously filtered on OrganizationId alone, which is a
        // weaker rule than every other device-targeted endpoint applies: an
        // administrator scoped to one group could queue an agent update -- an
        // installer running as SYSTEM -- on any machine in the tenant. Scope is
        // deny-by-default, so an account with no scope rows now reaches nothing.
        //
        // Answered as 404 rather than 403, matching the other device routes: a
        // caller who cannot act on a device is not told it exists.
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var release = await dbContext.AgentReleases
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == request.ReleaseId, cancellationToken);
        if (release is null || !release.IsPublished)
        {
            return Results.Problem(
                "The release does not exist or is not published.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var device = await dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId, cancellationToken);
        if (device is null)
        {
            return Results.NotFound();
        }

        if (!AgentVersionNumber.IsNewer(release.Version, device.AgentVersion))
        {
            return Results.Problem(
                $"Release {release.Version} is not newer than the installed agent "
                + $"{device.AgentVersion}. Downgrades and same-version reinstalls are not offered.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var task = await taskService.QueueAsync(
            actor.OrganizationId, deviceId, DeviceTaskType.UpdateAgent,
            new TaskPayloads.UpdateAgent(release.Id, release.Version, release.Sha256),
            actor.UserId, actor.Email, cancellationToken);

        return task is null
            ? Results.NotFound()
            : Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id });
    }
}
