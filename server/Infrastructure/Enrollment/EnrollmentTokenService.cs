using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Issues and revokes enrollment tokens on behalf of administrators.
/// </summary>
/// <remarks>
/// The token secret exists in server memory only inside <see cref="IssueAsync"/>:
/// generated, hashed for storage, returned once in the result, never logged. The
/// caller (Admin API endpoint) shows it to the administrator once; after that the
/// platform can only ever prove or disprove a presented value.
/// </remarks>
public sealed class EnrollmentTokenService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<EnrollmentTokenService> logger)
{
    /// <summary>Longest a token may live; a "forever" token is a standing credential.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<EnrollmentTokenService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IssuedEnrollmentToken> IssueAsync(
        Guid organizationId,
        string name,
        TimeSpan lifetime,
        int maxUses,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                lifetime,
                $"Token lifetime must be positive and at most {MaxLifetime.TotalDays:0} days.");
        }

        var now = _timeProvider.GetUtcNow();
        var secret = SecretGenerator.GenerateSecret();

        var token = new EnrollmentToken(
            organizationId,
            name,
            SecretGenerator.HashSecret(secret),
            actorId,
            actorDisplay,
            now + lifetime,
            maxUses);

        _dbContext.EnrollmentTokens.Add(token);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "enrollment_token.issue",
            AuditResult.Success,
            audit => audit
                .OnTarget("enrollment_token", token.Id.ToString(), name)
                // Metadata only. The secret and even its hash stay out of the trail.
                .WithStateChange(null, $$"""
                    {"name":{{System.Text.Json.JsonSerializer.Serialize(name)}},"expiresAt":"{{token.ExpiresAt:O}}","maxUses":{{maxUses}}}
                    """.Trim())
                .Requiring(Domain.Authorization.Permissions.Platform.EnrollmentTokenIssue));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Enrollment token {TokenId} ('{Name}') issued by {Actor}; expires {ExpiresAt}, max uses {MaxUses}.",
            token.Id,
            name,
            actorDisplay,
            token.ExpiresAt,
            maxUses);

        return new IssuedEnrollmentToken(token.Id, name, secret, token.ExpiresAt, maxUses);
    }

    public async Task<bool> RevokeAsync(
        Guid organizationId,
        Guid tokenId,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var token = await _dbContext.EnrollmentTokens
            .SingleOrDefaultAsync(
                t => t.Id == tokenId && t.OrganizationId == organizationId,
                cancellationToken);

        if (token is null)
        {
            return false;
        }

        token.Revoke(_timeProvider.GetUtcNow());

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "enrollment_token.revoke",
            AuditResult.Success,
            audit => audit
                .OnTarget("enrollment_token", token.Id.ToString(), token.Name)
                .Requiring(Domain.Authorization.Permissions.Platform.EnrollmentTokenRevoke));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Enrollment token {TokenId} revoked by {Actor}.", token.Id, actorDisplay);

        return true;
    }
}

/// <summary>
/// The result of issuing a token. <see cref="Secret"/> is the one and only
/// exposure of the secret value; callers must not log or persist it.
/// </summary>
public sealed record IssuedEnrollmentToken(
    Guid TokenId,
    string Name,
    string Secret,
    DateTimeOffset ExpiresAt,
    int MaxUses);
