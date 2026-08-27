using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Drivers;
using EndpointPlatform.Infrastructure.Drivers;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// The approved driver-package catalogue, and deploying a package to a device.
/// </summary>
/// <remarks>
/// <para>
/// Two different scopes, deliberately. Approving and withdrawing a package are
/// organization-level decisions about what the estate may run and carry no device, so
/// they are guarded by <c>driver.manage</c> alone -- the same shape as software
/// package upload. Deploying names a device and is therefore scope-checked like every
/// other device operation.
/// </para>
/// <para>
/// Nothing here installs anything. Deployment queues a typed task the endpoint pulls
/// on its next poll and verifies for itself; the server's approval is a necessary
/// condition, never a sufficient one.
/// </para>
/// </remarks>
public static class DriverPackageEndpoints
{
    public static IEndpointRouteBuilder MapDriverPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/driver-packages");

        group.MapGet("/", ListAsync)
            .WithName("ListDriverPackages")
            .RequirePermission(Permissions.Driver.View);

        group.MapPost("/", CreateAsync)
            .WithName("CreateDriverPackage")
            .RequirePermission(Permissions.Driver.Manage)
            .DisableAntiforgery(); // multipart upload; CSRF is covered by the X-Requested-With gate.

        group.MapPost("/{packageId:guid}/withdraw", WithdrawAsync)
            .WithName("WithdrawDriverPackage")
            .RequirePermission(Permissions.Driver.Manage);

        group.MapPost("/{packageId:guid}/deploy", DeployAsync)
            .WithName("DeployDriverPackage")
            .RequirePermission(Permissions.Driver.Manage);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        DriverPackageService service, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var packages = await service.ListAsync(organizationId, cancellationToken);

        return Results.Ok(packages.Select(p => new
        {
            p.Id,
            p.Name,
            p.Version,
            p.Provider,
            p.Sha256,
            p.FileName,
            p.SizeBytes,
            p.InfFileName,
            p.HardwareId,
            p.DriverVersion,
            p.RequiredSignerSubject,
            p.IsWithdrawn,
            p.CreatedByDisplay,
            p.CreatedAt,
        }));
    }

    private static async Task<IResult> CreateAsync(
        DriverPackageService service, HttpContext httpContext, CancellationToken cancellationToken)
    {
        RequestBodyLimits.AllowUploadOf(httpContext, DriverPackage.MaxArchiveBytes);

        if (!httpContext.Request.HasFormContentType)
        {
            return Results.Problem(
                "Expected a multipart/form-data upload.", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.Problem("A non-empty 'file' part is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > DriverPackage.MaxArchiveBytes)
        {
            return Results.Problem(
                "Driver package exceeds the maximum allowed size.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? Field(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

        var name = Field("name");
        var version = Field("version");
        var declaredSha256 = Field("sha256");
        var infFileName = Field("infFileName");
        var hardwareId = Field("hardwareId");
        var requiredSigner = Field("requiredSignerSubject");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(declaredSha256) || string.IsNullOrWhiteSpace(infFileName)
            || string.IsNullOrWhiteSpace(hardwareId))
        {
            return Results.Problem(
                "name, version, sha256, infFileName and hardwareId are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Called out separately from the other required fields because it is the one
        // whose absence is a security decision rather than a typo. A driver package
        // with no signer pin cannot be approved at all.
        if (string.IsNullOrWhiteSpace(requiredSigner))
        {
            return Results.Problem(
                "requiredSignerSubject is required for a driver package. A driver runs in the kernel, so "
                + "the publisher must be pinned rather than accepting any signature Windows happens to trust.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        await using var content = file.OpenReadStream();

        var result = await service.CreateAsync(
            actor.OrganizationId, name!, version!, Field("provider"), declaredSha256!,
            file.FileName, infFileName!, hardwareId!, Field("driverVersion"), requiredSigner!,
            content, actor.UserId, actor.Email, cancellationToken);

        return result.Status switch
        {
            DriverPackageCreateStatus.Created => Results.Ok(new
            {
                result.Package!.Id,
                result.Package.Name,
                result.Package.Version,
                result.Package.Sha256,
                result.Package.SizeBytes,
            }),

            DriverPackageCreateStatus.Duplicate =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

            DriverPackageCreateStatus.HashMismatch or DriverPackageCreateStatus.Invalid =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),

            _ => Results.Problem("Unhandled package outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> WithdrawAsync(
        Guid packageId, DriverPackageService service, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        return await service.WithdrawAsync(
            actor.OrganizationId, packageId, actor.UserId, actor.Email, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    public sealed record DeployRequest(Guid DeviceId, bool AllowDowngrade = false);

    private static async Task<IResult> DeployAsync(
        Guid packageId,
        DeployRequest request,
        DriverPackageService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = AdminActor.Required(httpContext.User);

        // Deployment names a device, so it is scope-checked like every other device
        // operation -- and, as elsewhere, an out-of-scope device is reported as
        // absent rather than forbidden.
        if (!await scope.CanActOnDeviceAsync(
                actor.UserId, actor.OrganizationId, request.DeviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.DeployAsync(
            actor.OrganizationId, packageId, request.DeviceId, request.AllowDowngrade,
            actor.UserId, actor.Email, cancellationToken);

        return result.Status switch
        {
            DriverDeployStatus.Queued => Results.Ok(new
            {
                taskId = result.Task!.Id,
                status = result.Task.Status.ToString(),
                expiresAt = result.Task.ExpiresAt,
                allowDowngrade = request.AllowDowngrade,
            }),

            DriverDeployStatus.PackageNotFound or DriverDeployStatus.DeviceNotFound => Results.NotFound(),

            // A real, actionable state rather than a not-found: the device exists and
            // the package is fine, but this endpoint cannot run the task yet.
            DriverDeployStatus.AgentTooOld =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

            _ => Results.Problem("Unhandled deploy outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
