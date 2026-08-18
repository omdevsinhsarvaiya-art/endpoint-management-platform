using EndpointPlatform.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Permission-based authorization: policies named <c>permission:&lt;key&gt;</c>
/// require the corresponding permission claim.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints declare <c>.RequirePermission(Permissions.Device.View)</c>. No code
/// anywhere checks a role name — roles exist only as grant bundles, resolved to
/// permission claims at authentication (see docs, RBAC section).
/// </para>
/// <para>
/// The policy provider refuses to build a policy for a permission key that is
/// not in the compiled catalogue, so a typo in an endpoint registration fails at
/// first use with a clear message instead of silently never matching any user.
/// </para>
/// </remarks>
public static class PermissionAuthorization
{
    public const string PolicyPrefix = "permission:";

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionKey)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.IsKnown(permissionKey))
        {
            throw new ArgumentException(
                $"'{permissionKey}' is not a permission in the catalogue (Permissions.cs).",
                nameof(permissionKey));
        }

        return builder.RequireAuthorization(PolicyPrefix + permissionKey);
    }
}

public sealed class PermissionRequirement(string permissionKey) : IAuthorizationRequirement
{
    public string PermissionKey { get; } = permissionKey;
}

public sealed class PermissionRequirementHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.HasClaim(AdminAuthenticationHandler.PermissionClaimType, requirement.PermissionKey))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Builds <c>permission:*</c> policies on demand; everything else falls back.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(PermissionAuthorization.PolicyPrefix, StringComparison.Ordinal))
        {
            var permissionKey = policyName[PermissionAuthorization.PolicyPrefix.Length..];

            if (!Permissions.IsKnown(permissionKey))
            {
                throw new InvalidOperationException(
                    $"Authorization policy '{policyName}' references unknown permission '{permissionKey}'.");
            }

            var policy = new AuthorizationPolicyBuilder(AdminAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permissionKey))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
