using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Enrollment;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Deciding which machines are allowed to become managed devices.
/// </summary>
/// <remarks>
/// <para>
/// An agent installed from the MSI carries no credential and no token. It asks to be
/// managed through the anonymous agent endpoint, and nothing happens until an
/// administrator approves it here. This is the authorization boundary for the whole
/// enrollment flow: the anonymous side can create a request and poll it, but only an
/// authenticated approval turns one into a device.
/// </para>
/// <para>
/// <b>The organization comes from the approver, never from the request.</b> A pending
/// request has no organization — an unauthenticated caller must not be able to choose
/// which tenant it joins — so the approving administrator's organization becomes the
/// device's. Cross-organization approval is therefore not something to defend against
/// here; it is impossible to express.
/// </para>
/// <para>
/// These endpoints hold no enrollment logic. Atomicity, double-approval safety and
/// expiry all live in <see cref="EnrollmentApprovalService"/> and
/// <see cref="PendingEnrollmentStore"/>, which the agent-facing side uses too.
/// </para>
/// </remarks>
public static class EnrollmentApprovalEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/enrollments");

        group.MapGet("/pending", ListPendingAsync)
            .WithName("ListPendingEnrollments")
            .RequirePermission(Permissions.Device.Enroll);

        group.MapPost("/{requestId}/approve", ApproveAsync)
            .WithName("ApproveEnrollment")
            .RequirePermission(Permissions.Device.Enroll);

        group.MapPost("/{requestId}/reject", RejectAsync)
            .WithName("RejectEnrollment")
            .RequirePermission(Permissions.Device.Enroll);

        return endpoints;
    }

    /// <summary>
    /// Machines waiting on a decision.
    /// </summary>
    /// <remarks>
    /// Returns only what an administrator needs to recognise the machine in front of
    /// them. The request id shown here is the SHA-256 the agent published; the secret
    /// behind it never leaves the endpoint, and the sealed enrollment token attached
    /// to an approved request is never projected.
    /// </remarks>
    private static async Task<IResult> ListPendingAsync(
        PendingEnrollmentStore pendingStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var pending = await pendingStore.ListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        return Results.Ok(pending.Select(entry => new
        {
            RequestId = entry.RequestId,
            entry.Request.Hostname,

            // Shown so an administrator can tell a re-enrolling machine from a new
            // one. Not a secret and not an authenticator - the server only uses it to
            // resolve an existing device record instead of creating a duplicate.
            entry.Request.MachineIdentifier,

            entry.Request.OperatingSystem,
            entry.Request.AgentVersion,
            entry.Request.RequestedAt,
            ExpiresAt = entry.Request.RequestedAt.Add(PendingEnrollment.Lifetime),
            Status = entry.Request.Status.ToString(),

            // Populated only once decided, and only ever a display name.
            entry.Request.ApprovedBy,

            // Deliberately absent: SealedTokenSecret, and anything derived from the
            // agent's proof secret beyond the request id it already published.
        }));
    }

    private static async Task<IResult> ApproveAsync(
        string requestId,
        HttpContext httpContext,
        EnrollmentApprovalService approvalService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var result = await approvalService.ApproveAsync(
            requestId, actor.OrganizationId, actor.UserId, actor.Email, cancellationToken);

        if (!result.Success)
        {
            // Expired, already decided, or lost a race with another administrator.
            // 409 rather than 404: the request may well have existed a moment ago, and
            // the caller's view of it is simply stale.
            return Results.Problem(
                title: result.FailureReason ?? "The enrollment request could not be approved.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new
        {
            RequestId = requestId,
            Status = nameof(PendingEnrollmentStatus.Approved),
            result.Request!.Hostname,
            Message = "The agent will collect its credential on its next poll.",
        });
    }

    private static async Task<IResult> RejectAsync(
        string requestId,
        HttpContext httpContext,
        EnrollmentApprovalService approvalService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var result = await approvalService.RejectAsync(
            requestId, actor.OrganizationId, actor.UserId, actor.Email, cancellationToken);

        if (!result.Success)
        {
            return Results.Problem(
                title: result.FailureReason ?? "The enrollment request could not be rejected.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new
        {
            RequestId = requestId,
            Status = nameof(PendingEnrollmentStatus.Rejected),
            result.Request!.Hostname,
            Message = "No credential will be issued for this machine.",
        });
    }
}
