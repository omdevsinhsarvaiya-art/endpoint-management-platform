using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Writes an audit entry whenever an authenticated administrator is DENIED by a
/// permission policy, then lets the default handler produce the 403.
/// </summary>
/// <remarks>
/// A denial is a security signal — it means a signed-in person tried something
/// their role does not allow — and the audit trail keeps <c>Denied</c> distinct
/// from <c>Failure</c> precisely so these are alertable. Anonymous 401s are not
/// audited here: unauthenticated probing is visible in request logs, and
/// auditing every scanner hit would let an attacker fill the audit table.
/// </remarks>
public sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.User.Identity?.IsAuthenticated == true)
        {
            var requiredPermissions = policy.Requirements
                .OfType<PermissionRequirement>()
                .Select(r => r.PermissionKey)
                .ToArray();

            var actor = AdminActor.FromClaims(context.User);

            if (actor is not null)
            {
                var auditWriter = context.RequestServices.GetRequiredService<AuditWriter>();

                await auditWriter.WriteImmediatelyAsync(
                    actor.OrganizationId,
                    AuditActorType.PlatformUser,
                    actor.UserId,
                    actor.Email,
                    action: "authz.denied",
                    AuditResult.Denied,
                    audit => audit
                        .Requiring(requiredPermissions.Length > 0 ? string.Join(",", requiredPermissions) : null)
                        .WithFailureReason(
                            $"{context.Request.Method} {context.Request.Path} requires a permission the caller does not hold."),
                    context.RequestAborted);
            }
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
