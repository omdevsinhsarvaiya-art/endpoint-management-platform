using System.Security.Claims;

namespace EndpointPlatform.Api.Security;

/// <summary>The authenticated administrator behind the current request, read from claims.</summary>
public sealed record AdminActor(Guid UserId, Guid OrganizationId, string Email)
{
    public static AdminActor? FromClaims(ClaimsPrincipal principal)
    {
        var userIdValue = principal.FindFirstValue(AdminAuthenticationHandler.UserIdClaimType);
        var organizationValue = principal.FindFirstValue(AdminAuthenticationHandler.OrganizationClaimType);
        var email = principal.FindFirstValue(ClaimTypes.Name);

        if (!Guid.TryParse(userIdValue, out var userId)
            || !Guid.TryParse(organizationValue, out var organizationId)
            || string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return new AdminActor(userId, organizationId, email);
    }

    /// <summary>
    /// The actor, or an exception: privileged endpoints run behind
    /// RequirePermission, so absent claims mean a wiring bug, not a user error.
    /// </summary>
    public static AdminActor Required(ClaimsPrincipal principal) =>
        FromClaims(principal)
        ?? throw new InvalidOperationException(
            "No authenticated administrator on a request that requires one. "
            + "Is the endpoint missing RequirePermission?");
}
