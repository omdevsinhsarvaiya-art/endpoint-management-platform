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

/// <summary>Why a password change was refused.</summary>
public enum ChangePasswordError
{
    /// <summary>The supplied current password did not verify, or the account is locked.</summary>
    CurrentPasswordIncorrect = 0,

    /// <summary>The new password does not meet <see cref="PasswordPolicy"/>.</summary>
    WeakPassword = 1,

    /// <summary>The new password is the one already in use.</summary>
    SameAsCurrent = 2,

    /// <summary>No such user, or the account has no password to change.</summary>
    NotPermitted = 3,
}

/// <param name="SessionsRevoked">
/// How many sessions the change invalidated, including the caller's own. Reported
/// so the console can say plainly that the caller must sign in again, rather than
/// leaving them to discover it on their next request.
/// </param>
public sealed record ChangePasswordOutcome(
    bool Success, ChangePasswordError? Error, string? Message, int SessionsRevoked)
{
    public static ChangePasswordOutcome Succeeded(int sessionsRevoked) =>
        new(true, null, null, sessionsRevoked);

    public static ChangePasswordOutcome Failed(ChangePasswordError error, string? message = null) =>
        new(false, error, message, 0);
}

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

    /// <summary>
    /// Changes the signed-in administrator's own password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current password is verified here rather than trusted from the
    /// session. A live session proves who the caller was at sign-in; it does not
    /// prove the person at the keyboard now is the same one. Requiring the
    /// current password is what stops a borrowed or stolen session from locking
    /// the real owner out of their own account.
    /// </para>
    /// <para>
    /// <b>Every existing session dies as a side effect, including the caller's.</b>
    /// <see cref="PlatformUser.SetPasswordHash"/> rotates the security stamp, and
    /// <see cref="AdminSession.IsUsable"/> compares each session's snapshot of
    /// that stamp against the user's current one. This is deliberate and is the
    /// property that makes a password change meaningful: if the reason for
    /// changing it is that the old one leaked, sessions minted with it must not
    /// survive. The caller signs in again like everyone else.
    /// </para>
    /// <para>
    /// A wrong current password counts towards the same lockout the sign-in path
    /// uses, so an authenticated attacker cannot brute-force it any more cheaply
    /// than an unauthenticated one. It is deliberately NOT behind the per-address
    /// login rate limiter: that limiter exists to blunt credential stuffing
    /// against an anonymous endpoint, and applying it here would let one noisy
    /// client block a legitimate administrator from securing their account.
    /// </para>
    /// </remarks>
    public async Task<ChangePasswordOutcome> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.PasswordHash is null)
        {
            return ChangePasswordOutcome.Failed(ChangePasswordError.NotPermitted);
        }

        if (user.IsLockedOut(now))
        {
            // Same answer as a wrong password, and for the same reason the
            // sign-in path gives it: distinguishing "locked" from "wrong" tells
            // an attacker whether their guessing is having an effect.
            await AuditPasswordChangeFailureAsync(user, "Account is locked out.", cancellationToken);
            return ChangePasswordOutcome.Failed(ChangePasswordError.CurrentPasswordIncorrect);
        }

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            user.RecordFailedSignIn(now, _options.LockoutThreshold, TimeSpan.FromMinutes(_options.LockoutMinutes));
            await AuditPasswordChangeFailureAsync(user, "Current password incorrect.", cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Password change refused for {UserId}: the current password did not verify.", user.Id);

            return ChangePasswordOutcome.Failed(ChangePasswordError.CurrentPasswordIncorrect);
        }

        if (PasswordPolicy.Validate(newPassword) is { } policyFailure)
        {
            // Not audited as a security failure and not counted towards lockout:
            // the caller has already proved who they are, and a weak-password
            // attempt is a usability event, not an attack.
            return ChangePasswordOutcome.Failed(ChangePasswordError.WeakPassword, policyFailure);
        }

        // Refused rather than silently accepted. Re-setting the same password
        // would rotate the stamp and destroy every session for no security gain,
        // which looks exactly like the platform malfunctioning.
        if (PasswordHasher.Verify(newPassword, user.PasswordHash))
        {
            return ChangePasswordOutcome.Failed(ChangePasswordError.SameAsCurrent);
        }

        // Rotates the security stamp, which is what invalidates every session.
        user.SetPasswordHash(PasswordHasher.Hash(newPassword), now);

        // Revoked explicitly as well as invalidated by the stamp. The stamp check
        // already makes them unusable; marking them revoked makes the reason
        // visible to anyone auditing the session table later, rather than leaving
        // rows that merely stopped working.
        var sessions = await _dbContext.AdminSessions
            .Where(x => x.PlatformUserId == user.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now);
        }

        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "platform.user.password_changed",
            AuditResult.Success,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                // No password material of any kind: not the old value, not the
                // new one, not a hash, not a length, not a prefix. The audit
                // records that a change happened, by whom, and what it cost the
                // caller in sessions -- nothing that helps anyone guess it.
                .WithStateChange(
                    System.Text.Json.JsonSerializer.Serialize(new { sessionsRevoked = 0 }),
                    System.Text.Json.JsonSerializer.Serialize(new { sessionsRevoked = sessions.Count })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Password changed for {UserId}; {SessionCount} session(s) revoked.", user.Id, sessions.Count);

        return ChangePasswordOutcome.Succeeded(sessions.Count);
    }

    private async Task AuditPasswordChangeFailureAsync(
        PlatformUser user, string reason, CancellationToken cancellationToken)
    {
        _auditWriter.Stage(
            user.OrganizationId,
            AuditActorType.PlatformUser,
            user.Id,
            user.Email,
            action: "platform.user.password_change_failed",
            AuditResult.Failure,
            audit => audit
                .OnTarget("platform_user", user.Id.ToString(), user.Email)
                .WithFailureReason(reason));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Verifies an already-signed-in administrator's current password, without
    /// changing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step-up authentication for operations where holding the permission is not
    /// enough -- revealing an escrowed BitLocker recovery key is the first. It
    /// answers the question "is this still the person who signed in", which a
    /// session cookie alone cannot.
    /// </para>
    /// <para>
    /// A wrong password counts towards the same lockout as a failed sign-in, and a
    /// locked-out account gets the same answer as a wrong password. Both match the
    /// sign-in and change-password paths, and for the same reason: telling a caller
    /// which of the two happened tells an attacker whether their guessing is
    /// working.
    /// </para>
    /// </remarks>
    public async Task<bool> VerifyCurrentPasswordAsync(
        Guid userId, string currentPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        var user = await _dbContext.PlatformUsers
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.PasswordHash is null || user.IsLockedOut(now))
        {
            return false;
        }

        if (PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return true;
        }

        user.RecordFailedSignIn(now, _options.LockoutThreshold, TimeSpan.FromMinutes(_options.LockoutMinutes));
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Step-up password verification failed for {UserId}.", userId);
        return false;
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
