using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Administrator management of enrollment tokens. Every route is guarded by a
/// permission policy; the acting administrator comes from the session claims.
/// </summary>
public static class EnrollmentTokenEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentTokenEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/enrollment-tokens");

        group.MapPost("/", IssueAsync)
            .WithName("IssueEnrollmentToken")
            .RequirePermission(Permissions.Platform.EnrollmentTokenIssue);

        group.MapGet("/", ListAsync)
            .WithName("ListEnrollmentTokens")
            .RequirePermission(Permissions.Platform.EnrollmentTokenView);

        group.MapPost("/{tokenId:guid}/revoke", RevokeAsync)
            .WithName("RevokeEnrollmentToken")
            .RequirePermission(Permissions.Platform.EnrollmentTokenRevoke);

        return endpoints;
    }

    public sealed record IssueTokenRequest(string Name, int LifetimeHours, int MaxUses);

    public sealed record IssuedTokenResponse(
        Guid TokenId,
        string Name,
        string Secret,
        DateTimeOffset ExpiresAt,
        int MaxUses,
        string Warning);

    public sealed record TokenListItem(
        Guid Id,
        string Name,
        string CreatedByDisplay,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int MaxUses,
        int UseCount,
        bool IsRevoked,
        bool IsUsable);

    private static async Task<IResult> IssueAsync(
        [FromBody] IssueTokenRequest request,
        EnrollmentTokenService tokenService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            return Results.Problem(
                title: "Token name is required and must be at most 200 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.LifetimeHours < 1 || TimeSpan.FromHours(request.LifetimeHours) > EnrollmentTokenService.MaxLifetime)
        {
            return Results.Problem(
                title: $"Lifetime must be between 1 hour and {EnrollmentTokenService.MaxLifetime.TotalDays:0} days.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.MaxUses is < 1 or > 10_000)
        {
            return Results.Problem(
                title: "Max uses must be between 1 and 10,000.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);

        var issued = await tokenService.IssueAsync(
            actor.OrganizationId,
            request.Name.Trim(),
            TimeSpan.FromHours(request.LifetimeHours),
            request.MaxUses,
            actor.UserId,
            actor.Email,
            cancellationToken);

        return Results.Ok(new IssuedTokenResponse(
            issued.TokenId,
            issued.Name,
            issued.Secret,
            issued.ExpiresAt,
            issued.MaxUses,
            Warning: "This secret is shown exactly once. Store it securely; the server keeps only a hash."));
    }

    private static async Task<IResult> ListAsync(
        EndpointPlatformDbContext dbContext,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var now = timeProvider.GetUtcNow();

        // Note what is NOT selected: the secret hash never leaves the database.
        var tokens = await dbContext.EnrollmentTokens
            .AsNoTracking()
            .Where(t => t.OrganizationId == organizationId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(200)
            .Select(t => new TokenListItem(
                t.Id,
                t.Name,
                t.CreatedByDisplay,
                t.CreatedAt,
                t.ExpiresAt,
                t.MaxUses,
                t.UseCount,
                t.RevokedAt != null,
                t.RevokedAt == null && now < t.ExpiresAt && t.UseCount < t.MaxUses))
            .ToListAsync(cancellationToken);

        return Results.Ok(tokens);
    }

    private static async Task<IResult> RevokeAsync(
        Guid tokenId,
        EnrollmentTokenService tokenService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var revoked = await tokenService.RevokeAsync(
            actor.OrganizationId, tokenId, actor.UserId, actor.Email, cancellationToken);

        return revoked ? Results.NoContent() : Results.NotFound();
    }
}
