using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Sign-in, sign-out and per-request session validation for the Admin API.
/// </summary>
/// <remarks>
/// <para>
/// Sign-in failure behaviour: the caller always receives the same generic
/// failure whether the account is unknown, the password wrong, the account
/// disabled or locked — the distinctions live in the audit trail. A dummy
/// PBKDF2 verification runs for unknown accounts so response timing does not
/// reveal which addresses exist.
/// </para>
/// <para>
/// Every sign-in attempt, success or failure, is audited. Failures are written
/// immediately (nothing else commits); successes commit atomically with the
/// session row.
/// </para>
/// </remarks>
public sealed class AdminAuthService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AdminAuthOptions> options,
    ILogger<AdminAuthService> logger)
{
    /// <summary>A real hash of an unguessable value, used to equalise timing for unknown accounts.</summary>
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly AdminAuthOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<AdminAuthService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<SignInOutcome> SignInAsync(
        string email,
        string password,
        string? sourceIp,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var normalized = email.Trim().ToUpperInvariant();

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (user is null)
        {
            // Equalise timing with the real-verification path.
            PasswordHasher.Verify(password, DummyHash);
            await AuditSignInFailureAsync(null, email, "Unknown account.", cancellationToken);
            return SignInOutcome.Failed();
        }

        if (user.Status == PlatformUserStatus.Disabled)
        {
            PasswordHasher.Verify(password, DummyHash);
            await AuditSignInFailureAsync(user, email, "Account is disabled.", cancellationToken);
            return SignInOutcome.Failed();
        }

        if (user.IsLockedOut(now))
        {
            PasswordHasher.Verify(password, DummyHash);
            await AuditSignInFailureAsync(user, email, "Account is locked out.", cancellationToken);
            return SignInOutcome.Failed();
        }

        if (user.PasswordHash is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            user.RecordFailedSignIn(now, _options.LockoutThreshold, TimeSpan.FromMinutes(_options.LockoutMinutes));
            await AuditSignInFailureAsync(user, email, "Wrong password.", cancellationToken);
            return SignInOutcome.Failed();
        }

        // Success. Opportunistically upgrade the stored hash if policy has moved on.
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            user.SetPasswordHash(PasswordHasher.Hash(password), now);
        }

        user.RecordSuccessfulSignIn(now);

        var token = SecretGenerator.GenerateSecret();

        var session = new AdminSession(
            user.Id,
            SecretGenerator.HashSecret(token),
            user.SecurityStamp,
            now,
            now.AddHours(_options.SessionLifetimeHours),
            sourceIp,
            userAgent);

        _dbContext.AdminSessions.Add(session);

        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "auth.sign_in",
            AuditResult.Success);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var permissions = await ResolvePermissionsAsync(user.Id, cancellationToken);

        return SignInOutcome.Succeeded(user, token, session.ExpiresAt, permissions);
    }

    /// <summary>Validates a presented session token; null when it is not acceptable.</summary>
    public async Task<AuthenticatedAdmin?> ValidateSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var tokenHash = SecretGenerator.HashSecret(token);

        var session = await _dbContext.AdminSessions
            .Include(s => s.PlatformUser)
            .SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        if (session?.PlatformUser is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var user = session.PlatformUser;

        if (user.Status != PlatformUserStatus.Active || !session.IsUsable(now, user.SecurityStamp))
        {
            return null;
        }

        session.Touch(now);
        // Persisted alongside whatever the request itself saves; a read-only
        // request skipping the touch write is acceptable.

        var permissions = await ResolvePermissionsAsync(user.Id, cancellationToken);

        return new AuthenticatedAdmin(user.Id, user.OrganizationId, user.Email, user.DisplayName, permissions);
    }

    public async Task SignOutAsync(string token, CancellationToken cancellationToken = default)
    {
        var tokenHash = SecretGenerator.HashSecret(token);

        var session = await _dbContext.AdminSessions
            .Include(s => s.PlatformUser)
            .SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        if (session is null)
        {
            return; // Signing out an unknown token is a no-op, not an error.
        }

        session.Revoke(_timeProvider.GetUtcNow());

        if (session.PlatformUser is { } user)
        {
            _auditWriter.Stage(
                user.OrganizationId,
                AuditActorType.PlatformUser,
                user.Id,
                user.Email,
                action: "auth.sign_out",
                AuditResult.Success);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Effective permission keys: user → roles → role permissions.</summary>
    private async Task<IReadOnlyList<string>> ResolvePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.PlatformUserRoles
            .Where(ur => ur.PlatformUserId == userId)
            .Join(_dbContext.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (_, rp) => rp.PermissionId)
            .Join(_dbContext.Permissions, id => id, p => p.Id, (_, p) => p.Key)
            .Distinct()
            .OrderBy(key => key)
            .ToListAsync(cancellationToken);
    }

    private async Task AuditSignInFailureAsync(
        PlatformUser? user,
        string attemptedEmail,
        string reason,
        CancellationToken cancellationToken)
    {
        var organizationId = user?.OrganizationId
            ?? await _dbContext.Organizations
                .OrderBy(o => o.CreatedAt)
                .Select(o => (Guid?)o.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (organizationId is null)
        {
            _logger.LogWarning("Sign-in failed ({Reason}) and no organization exists to audit against.", reason);
            return;
        }

        // WriteImmediately: the failed-attempt counter on the user (if any) must
        // persist even though the sign-in itself produced nothing else to save.
        await _auditWriter.WriteImmediatelyAsync(
            organizationId.Value,
            user is null ? AuditActorType.Anonymous : AuditActorType.PlatformUser,
            user?.Id,
            attemptedEmail.Trim(),
            action: "auth.sign_in",
            AuditResult.Failure,
            audit => audit.WithFailureReason(reason),
            cancellationToken);

        _logger.LogWarning("Admin sign-in failed for {Email}: {Reason}", attemptedEmail, reason);
    }
}

public sealed record SignInOutcome(
    bool Success,
    PlatformUser? User,
    string? Token,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Permissions)
{
    public static SignInOutcome Failed() => new(false, null, null, default, []);

    public static SignInOutcome Succeeded(
        PlatformUser user, string token, DateTimeOffset expiresAt, IReadOnlyList<string> permissions) =>
        new(true, user, token, expiresAt, permissions);
}

/// <summary>The authenticated principal attached to a validated request.</summary>
public sealed record AuthenticatedAdmin(
    Guid UserId,
    Guid OrganizationId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Permissions);
