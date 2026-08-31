using System.ComponentModel.DataAnnotations;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.BitLocker;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// BitLocker recovery-key escrow: file a recovery password, see that one exists,
/// reveal it deliberately, and delete it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Listing never returns a key, plaintext or sealed.</b> The list projection
/// below has no field for either, so a caller with <c>bitlocker.view</c> learns
/// that a key exists, which protector it covers and who filed it -- and nothing
/// more. Retrieval is a separate route, a separate permission, and a POST.
/// </para>
/// <para>
/// <b>Reveal is POST and never GET.</b> A GET would put the operation in browser
/// history, proxy logs and <c>Referer</c> headers, and it cannot carry the body
/// this route requires. No escrow id, password or key ever appears in a URL or a
/// query string.
/// </para>
/// <para>
/// Reveal passes four independent gates before a key is decrypted: the
/// <c>bitlocker.recovery_key.read</c> permission, the device scope check, a rate
/// limit on both the caller and the device, and step-up re-verification of the
/// caller's own password. Every outcome is audited, including the refusals.
/// </para>
/// </remarks>
public static class BitLockerEscrowEndpoints
{
    public static IEndpointRouteBuilder MapBitLockerEscrowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var device = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}");

        device.MapGet("/bitlocker-escrows", ListAsync)
            .WithName("ListBitLockerEscrows")
            .RequirePermission(Permissions.BitLocker.View);

        device.MapPost("/bitlocker-escrows", EscrowAsync)
            .WithName("EscrowBitLockerRecoveryKey")
            .RequirePermission(Permissions.BitLocker.RecoveryKeyManage);

        var escrow = endpoints.MapGroup("/admin/v1/bitlocker-escrows");

        // POST, deliberately. See the type remarks.
        escrow.MapPost("/{escrowId:guid}/reveal", RevealAsync)
            .WithName("RevealBitLockerRecoveryKey")
            .RequirePermission(Permissions.BitLocker.RecoveryKeyRead);

        escrow.MapDelete("/{escrowId:guid}", DeleteAsync)
            .WithName("DeleteBitLockerRecoveryKey")
            .RequirePermission(Permissions.BitLocker.RecoveryKeyManage);

        // Automatic-escrow retry state.
        //
        // Addressed by ATTEMPT id, not escrow id, and the difference is not
        // cosmetic: an escrow row exists only once a key has been filed, so a
        // protector that exhausted its attempts -- the exact case reset exists for
        // -- has no escrow to name. Keying the route on escrows would have made it
        // unable to reach anything it was built to fix.
        device.MapGet("/bitlocker-escrow-attempts", ListAttemptsAsync)
            .WithName("ListBitLockerEscrowAttempts")
            .RequirePermission(Permissions.BitLocker.View);

        endpoints.MapGroup("/admin/v1/bitlocker-escrow-attempts")
            .MapPost("/{attemptId:guid}/reset", ResetAttemptsAsync)
            .WithName("ResetBitLockerEscrowAttempts")
            .RequirePermission(Permissions.BitLocker.RecoveryKeyManage);

        return endpoints;
    }

    // ------------------------------------------------------------------ list

    private static async Task<IResult> ListAsync(
        Guid deviceId,
        RecoveryEscrowService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var escrows = await service.ListAsync(actor.OrganizationId, deviceId, cancellationToken);

        // Metadata only. There is deliberately no property here that could carry
        // the key or its ciphertext, so the route cannot leak one by omission.
        return Results.Ok(escrows.Select(e => new
        {
            id = e.Id,
            volumeDeviceIdentifier = e.VolumeDeviceIdentifier,
            keyProtectorId = e.KeyProtectorId,
            driveLetter = e.DriveLetter,
            isActive = e.IsActive,

            // Which mechanism filed this. Without it the console cannot tell an
            // endpoint-collected key from one an administrator typed, and was
            // rendering automatic escrows under the manual heading -- offering
            // Replace and Delete for a record no administrator owns.
            origin = e.Origin.ToString(),

            escrowedAt = e.EscrowedAt,
            escrowedBy = e.EscrowedByDisplay,
            supersededAt = e.SupersededAt,
            revealedCount = e.RevealedCount,
            lastRevealedAt = e.LastRevealedAt,
        }));
    }

    // ---------------------------------------------------------------- escrow

    /// <param name="RecoveryPassword">
    /// The 48-digit password. Validated server-side; the client's check is UX.
    /// Present in this request body and nowhere else in the platform.
    /// </param>
    public sealed record EscrowRequest(
        [property: Required, StringLength(256, MinimumLength = 1)] string? VolumeDeviceIdentifier,
        [property: Required, StringLength(64, MinimumLength = 1)] string? KeyProtectorId,
        [property: Required, StringLength(80, MinimumLength = 1)] string? RecoveryPassword);

    private static async Task<IResult> EscrowAsync(
        Guid deviceId,
        EscrowRequest request,
        RecoveryEscrowService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var result = await service.EscrowAsync(
            actor.OrganizationId, deviceId, request.VolumeDeviceIdentifier!, request.KeyProtectorId!,
            request.RecoveryPassword!, actor.UserId, actor.Email, cancellationToken);

        return result.Outcome switch
        {
            EscrowOutcome.Success => Results.Ok(new
            {
                id = result.Escrow!.Id,
                keyProtectorId = result.Escrow.KeyProtectorId,
                escrowedAt = result.Escrow.EscrowedAt,
            }),

            EscrowOutcome.DeviceNotFound or EscrowOutcome.VolumeNotFound => Results.NotFound(),

            EscrowOutcome.InvalidRecoveryPassword =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),

            EscrowOutcome.Conflict =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

            _ => Results.Problem("Unhandled escrow outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    // ---------------------------------------------------------------- reveal

    /// <param name="CurrentPassword">
    /// The caller's own password, re-verified here. Holding the permission proves
    /// what the account may do; this proves the account is still being driven by
    /// the person who signed in.
    /// </param>
    /// <param name="Justification">Recorded in the audit trail. Not optional.</param>
    public sealed record RevealRequest(
        [property: Required, StringLength(256, MinimumLength = 1)] string? CurrentPassword,
        [property: Required, StringLength(500, MinimumLength = 3)] string? Justification);

    private static async Task<IResult> RevealAsync(
        Guid escrowId,
        RevealRequest request,
        RecoveryEscrowService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var actor = AdminActor.Required(httpContext.User);

        // Scope is checked against the escrow's own device, not against anything
        // the caller supplied: the route names an escrow, and an administrator
        // scoped to one group must not reach another group's key by quoting its id.
        var escrow = await service.FindAsync(actor.OrganizationId, escrowId, cancellationToken);
        if (escrow is null)
        {
            return Results.NotFound();
        }

        if (!await scope.CanActOnDeviceAsync(
                actor.UserId, actor.OrganizationId, escrow.DeviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var result = await service.RevealAsync(
            actor.OrganizationId, escrowId, actor.UserId, actor.Email,
            request.CurrentPassword!, request.Justification!, cancellationToken);

        switch (result.Outcome)
        {
            case EscrowOutcome.Success:
                // The one response in the platform that carries key material.
                // Marked no-store so no cache, proxy or service worker retains it.
                httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                httpContext.Response.Headers.Pragma = "no-cache";

                return Results.Ok(new
                {
                    escrowId = escrow.Id,
                    keyProtectorId = escrow.KeyProtectorId,
                    driveLetter = escrow.DriveLetter,
                    recoveryPassword = result.RecoveryPassword,
                });

            case EscrowOutcome.RateLimited:
                httpContext.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
                return Results.Problem(result.Error, statusCode: StatusCodes.Status429TooManyRequests);

            // Same status and shape as a wrong password on the sign-in path, and
            // for the same reason: the caller learns that it failed, not which
            // check failed.
            case EscrowOutcome.StepUpFailed:
                return Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden);

            case EscrowOutcome.AlreadyDeleted:
                return Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict);

            case EscrowOutcome.NotFound:
                return Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound);

            default:
                return Results.Problem("Unhandled reveal outcome.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ---------------------------------------------------------------- delete

    private static async Task<IResult> DeleteAsync(
        Guid escrowId,
        RecoveryEscrowService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var escrow = await service.FindAsync(actor.OrganizationId, escrowId, cancellationToken);
        if (escrow is null)
        {
            return Results.NotFound();
        }

        if (!await scope.CanActOnDeviceAsync(
                actor.UserId, actor.OrganizationId, escrow.DeviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var result = await service.DeleteAsync(
            actor.OrganizationId, escrowId, actor.UserId, actor.Email, cancellationToken);

        return result.Outcome switch
        {
            EscrowOutcome.Success => Results.NoContent(),
            EscrowOutcome.NotFound => Results.NotFound(),
            EscrowOutcome.AlreadyDeleted =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("Unhandled delete outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    // ----------------------------------------------------------------- shared

    private static IResult? Validate<T>(T request) where T : notnull
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return null;
        }

        // Reports which member failed, never the value it carried -- one of these
        // members is a recovery password and another is the caller's own password.
        return Results.ValidationProblem(results.ToDictionary(
            v => v.MemberNames.FirstOrDefault() ?? "request",
            v => new[] { v.ErrorMessage ?? "Invalid." }));
    }

    /// <summary>
    /// Deliberately 404, matching the elevation, driver and BitLocker endpoints:
    /// an administrator who cannot reach a device should not learn it exists.
    /// </summary>
    private static IResult OutOfScope() => Results.NotFound();
    // ------------------------------------------- automatic escrow retry state

    /// <summary>
    /// Automatic-escrow status for a device's protectors.
    /// </summary>
    /// <remarks>
    /// Under <c>bitlocker.view</c> rather than a key permission: this reports where
    /// collection has got to, and carries no key material of any kind. Someone who
    /// may see that a machine is encrypted may see whether its key was filed.
    /// </remarks>
    private static async Task<IResult> ListAttemptsAsync(
        Guid deviceId,
        Infrastructure.BitLocker.EscrowAttemptAdminService attempts,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        return Results.Ok(await attempts.GetStatusAsync(actor.OrganizationId, deviceId, cancellationToken));
    }

    /// <summary>
    /// Re-arms automatic escrow for one protector that stopped retrying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires <c>recovery_key.manage</c> and device scope, and is audited. Scope
    /// is resolved from the attempt's own device rather than anything the caller
    /// supplies, so quoting another group's attempt id yields a 404 rather than a
    /// reset.
    /// </para>
    /// <para>
    /// <b>This grants no access to any key.</b> It clears a failure count so the
    /// endpoint may try again; the recovery password is neither read, returned nor
    /// touched, and revealing one still requires the separate permission, the
    /// step-up password and the reveal rate limiter.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ResetAttemptsAsync(
        Guid attemptId,
        Infrastructure.BitLocker.EscrowAttemptAdminService attempts,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var attempt = await attempts.FindAsync(actor.OrganizationId, attemptId, cancellationToken);

        if (attempt is null)
        {
            return Results.NotFound();
        }

        if (!await scope.CanActOnDeviceAsync(
                actor.UserId, actor.OrganizationId, attempt.DeviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var outcome = await attempts.ResetAsync(
            actor.OrganizationId, attemptId, actor.UserId, actor.Email, cancellationToken);

        return outcome switch
        {
            Infrastructure.BitLocker.EscrowResetOutcome.Reset =>
                Results.Ok(new { status = "reset" }),

            Infrastructure.BitLocker.EscrowResetOutcome.NotExhausted =>
                Results.Problem(
                    title: "This protector is not in a stopped state, so there is nothing to re-arm.",
                    statusCode: StatusCodes.Status409Conflict),

            _ => Results.NotFound(),
        };
    }
}
