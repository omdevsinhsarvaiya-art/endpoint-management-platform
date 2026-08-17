using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Administrator management of enrollment tokens.
/// </summary>
/// <remarks>
/// SECURITY NOTE (Phase 1): the Admin API has no authentication until Phase 3, so
/// these endpoints currently execute as a synthetic "development administrator"
/// actor and MUST NOT be exposed beyond localhost. This is recorded in the threat
/// model's known-limitations table. When Phase 3 lands, the synthetic actor is
/// replaced by the authenticated principal and permission enforcement
/// (<c>platform.enrollment_token.issue</c> / <c>.revoke</c>) guards each route.
/// </remarks>
public static class EnrollmentTokenEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentTokenEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/enrollment-tokens");

        group.MapPost("/", IssueAsync).WithName("IssueEnrollmentToken");
        group.MapGet("/", ListAsync).WithName("ListEnrollmentTokens");
        group.MapPost("/{tokenId:guid}/revoke", RevokeAsync).WithName("RevokeEnrollmentToken");

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
        EndpointPlatformDbContext dbContext,
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

        var organizationId = await GetDefaultOrganizationIdAsync(dbContext, cancellationToken);
        if (organizationId is null)
        {
            return Results.Problem(
                title: "The platform has not been seeded; run the migration job first.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var (actorId, actorDisplay) = DevelopmentActor.Get();

        var issued = await tokenService.IssueAsync(
            organizationId.Value,
            request.Name.Trim(),
            TimeSpan.FromHours(request.LifetimeHours),
            request.MaxUses,
            actorId,
            actorDisplay,
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
        CancellationToken cancellationToken)
    {
        var organizationId = await GetDefaultOrganizationIdAsync(dbContext, cancellationToken);
        if (organizationId is null)
        {
            return Results.Ok(Array.Empty<TokenListItem>());
        }

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
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var organizationId = await GetDefaultOrganizationIdAsync(dbContext, cancellationToken);
        if (organizationId is null)
        {
            return Results.NotFound();
        }

        var (actorId, actorDisplay) = DevelopmentActor.Get();

        var revoked = await tokenService.RevokeAsync(
            organizationId.Value, tokenId, actorId, actorDisplay, cancellationToken);

        return revoked ? Results.NoContent() : Results.NotFound();
    }

    private static Task<Guid?> GetDefaultOrganizationIdAsync(
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .OrderBy(o => o.CreatedAt)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// The synthetic actor used until Phase 3 delivers authentication. A fixed,
/// recognisable identity — audit entries recorded against it are clearly
/// attributable to the unauthenticated development window, not mistaken for a
/// real administrator.
/// </summary>
internal static class DevelopmentActor
{
    private static readonly Guid ActorId = new("00000000-0000-0000-0000-00000000dead");

    public static (Guid ActorId, string ActorDisplay) Get() =>
        (ActorId, "development-unauthenticated@localhost");
}
