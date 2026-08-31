using System.Security.Cryptography;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.BitLocker;

public enum EscrowOutcome
{
    Success = 0,
    DeviceNotFound = 1,
    VolumeNotFound = 2,
    InvalidRecoveryPassword = 3,
    NotFound = 4,
    AlreadyDeleted = 5,
    StepUpFailed = 6,
    RateLimited = 7,
    Conflict = 8,
}

/// <param name="Error">Safe to show a caller. Never contains key material.</param>
public sealed record EscrowResult(EscrowOutcome Outcome, BitLockerRecoveryEscrow? Escrow, string? Error);

/// <param name="RecoveryPassword">
/// The plaintext, present only on a successful reveal and only in memory on the
/// way to the HTTP response. Never persisted, logged or audited.
/// </param>
public sealed record RevealResult(
    EscrowOutcome Outcome, string? RecoveryPassword, string? Error, int RetryAfterSeconds);

/// <summary>
/// Escrows, supersedes, reveals and deletes BitLocker recovery passwords.
/// </summary>
/// <remarks>
/// <para>
/// This is the only type in the platform that handles a plaintext recovery
/// password, and it holds one for as short a time as possible: on the way in it is
/// validated and sealed immediately, on the way out it is unsealed and returned to
/// the caller. It is never written to a log, an exception, an audit row, a task
/// payload or an inventory record, and the domain entity it produces has nowhere
/// to put one.
/// </para>
/// <para>
/// Every audit document is built through <see cref="AuditStateRedactor"/> rather
/// than by hand. The audit trail is append-only and enforced by database triggers,
/// so a secret written into it cannot be removed afterwards -- redaction has to be
/// structural rather than remembered.
/// </para>
/// </remarks>
public sealed class RecoveryEscrowService(
    EndpointPlatformDbContext dbContext,
    IRecoveryKeyProtector protector,
    IHybridEnvelopeUnsealer hybridUnsealer,
    RevealRateLimiter rateLimiter,
    AdminAuthService authService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<RecoveryEscrowService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IRecoveryKeyProtector _protector = protector
        ?? throw new ArgumentNullException(nameof(protector));

    private readonly IHybridEnvelopeUnsealer _hybridUnsealer = hybridUnsealer
        ?? throw new ArgumentNullException(nameof(hybridUnsealer));

    private readonly RevealRateLimiter _rateLimiter = rateLimiter
        ?? throw new ArgumentNullException(nameof(rateLimiter));

    private readonly AdminAuthService _authService = authService
        ?? throw new ArgumentNullException(nameof(authService));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<RecoveryEscrowService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // ---------------------------------------------------------------- escrow

    /// <summary>
    /// Escrows a recovery password, superseding any active escrow for the same
    /// device, volume and protector.
    /// </summary>
    public async Task<EscrowResult> EscrowAsync(
        Guid organizationId,
        Guid deviceId,
        string volumeDeviceIdentifier,
        string keyProtectorId,
        string recoveryPassword,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        // Validated server-side before anything else. The client's copy of this
        // rule is a convenience; this one is the control.
        if (BitLockerRecoveryPassword.Validate(recoveryPassword) is var error
            && error != RecoveryPasswordError.None)
        {
            return new EscrowResult(EscrowOutcome.InvalidRecoveryPassword, null,
                BitLockerRecoveryPassword.Describe(error));
        }

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null)
        {
            return new EscrowResult(EscrowOutcome.DeviceNotFound, null, "No such device.");
        }

        // The volume must be one the endpoint actually reported. Escrowing against
        // a volume nobody has seen is how a key ends up filed under the wrong
        // machine and is discovered to be useless at the worst moment.
        var volume = await _dbContext.DeviceBitLockerVolumes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                v => v.DeviceId == deviceId && v.DeviceIdentifier == volumeDeviceIdentifier,
                cancellationToken);

        if (volume is null)
        {
            return new EscrowResult(EscrowOutcome.VolumeNotFound, null,
                "This endpoint has not reported a volume with that identifier.");
        }

        var now = _timeProvider.GetUtcNow();

        BitLockerRecoveryEscrow escrow;
        try
        {
            escrow = new BitLockerRecoveryEscrow(
                organizationId, deviceId, volumeDeviceIdentifier, keyProtectorId, volume.DriveLetter,
                _protector.Protect(recoveryPassword), _protector.CurrentKeyVersion,
                actorId, actorDisplay, now);
        }
        catch (ArgumentException ex)
        {
            // Message names the field, never the value.
            return new EscrowResult(EscrowOutcome.InvalidRecoveryPassword, null, ex.Message);
        }

        var existing = await _dbContext.BitLockerRecoveryEscrows
            .SingleOrDefaultAsync(
                e => e.DeviceId == deviceId
                    && e.VolumeDeviceIdentifier == volumeDeviceIdentifier
                    && e.KeyProtectorId == escrow.KeyProtectorId
                    && e.IsActive,
                cancellationToken);

        var replaced = existing is not null;

        // One transaction, two saves, and the ORDER matters.
        //
        // The partial unique index admits one active row per
        // device+volume+protector and is checked per statement rather than at
        // commit -- PostgreSQL cannot defer a partial index. EF emits inserts
        // before updates, so superseding the old row and inserting the new one in
        // a single save briefly leaves two active rows and trips the constraint on
        // every legitimate replacement. The old record therefore has to stand down
        // and be flushed before the new one is added to the change tracker at all.
        //
        // Driven by the execution strategy rather than a bare BeginTransaction:
        // retry-on-failure is configured, and EF refuses a user-initiated
        // transaction under a retrying strategy, because the strategy has to own
        // the retry to replay the whole unit rather than half of it.
        var strategy = _dbContext.Database.CreateExecutionStrategy();

        var conflicted = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction =
                await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                if (existing is not null)
                {
                    existing.TrySupersede(escrow.Id, now);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                _dbContext.BitLockerRecoveryEscrows.Add(escrow);

                StageAudit(
                    device, actorId, actorDisplay,
                    replaced ? "bitlocker.recovery_key.replaced" : "bitlocker.recovery_key.escrowed",
                    AuditResult.Success,
                    previous: replaced
                        ? new Dictionary<string, object?>
                        {
                            ["escrowId"] = existing!.Id,
                            ["keyVersion"] = existing.KeyVersion,
                            ["escrowedAt"] = existing.EscrowedAt,
                        }
                        : null,
                    next: Describe(escrow));

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Reached only by a genuine race: another request escrowed the same
                // protector between the read above and this write. The index is the
                // authoritative guarantee; the earlier check is a courtesy that two
                // concurrent callers can both pass.
                await transaction.RollbackAsync(cancellationToken);
                return true;
            }
        });

        if (conflicted)
        {
            return new EscrowResult(EscrowOutcome.Conflict, null,
                "Another escrow for this volume and protector was created concurrently. Retry.");
        }

        _logger.LogInformation(
            "Recovery key escrowed for device {DeviceId} volume {Volume} protector {Protector} (replaced: {Replaced}).",
            deviceId, volumeDeviceIdentifier, escrow.KeyProtectorId, replaced);

        return new EscrowResult(EscrowOutcome.Success, escrow, null);
    }

    // ---------------------------------------------------------------- reveal

    /// <summary>
    /// Reveals an escrowed recovery password, after rate limiting and step-up.
    /// </summary>
    /// <remarks>
    /// The order is deliberate: rate limit first so a caller cannot use this
    /// endpoint to brute-force the administrator password without bound, then
    /// step-up, then decrypt. Every refusal is audited with its reason.
    /// </remarks>
    public async Task<RevealResult> RevealAsync(
        Guid organizationId,
        Guid escrowId,
        Guid actorId,
        string actorDisplay,
        string currentPassword,
        string justification,
        CancellationToken cancellationToken = default)
    {
        var escrow = await _dbContext.BitLockerRecoveryEscrows
            .SingleOrDefaultAsync(e => e.Id == escrowId && e.OrganizationId == organizationId, cancellationToken);

        if (escrow is null)
        {
            return new RevealResult(EscrowOutcome.NotFound, null, "No such escrow record.", 0);
        }

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == escrow.DeviceId, cancellationToken);

        var limit = await _rateLimiter.TryConsumeAsync(actorId, escrow.DeviceId, cancellationToken);
        if (!limit.Allowed)
        {
            StageAudit(device, actorId, actorDisplay, "bitlocker.recovery_key.reveal_denied",
                AuditResult.Failure,
                previous: null,
                next: new Dictionary<string, object?>
                {
                    ["escrowId"] = escrow.Id,
                    ["reason"] = "rate limited",
                    ["scope"] = limit.Scope,
                },
                failureReason: $"Rate limited ({limit.Scope}).");

            await _dbContext.SaveChangesAsync(cancellationToken);

            return new RevealResult(EscrowOutcome.RateLimited, null,
                "Too many recovery-key reveals. Try again later.", limit.RetryAfterSeconds);
        }

        // Step-up: holding the permission is not enough, the caller must prove they
        // are still the person who signed in. Reuses the sign-in verifier, so a
        // wrong password counts towards the same lockout.
        if (!await _authService.VerifyCurrentPasswordAsync(actorId, currentPassword, cancellationToken))
        {
            StageAudit(device, actorId, actorDisplay, "bitlocker.recovery_key.reveal_denied",
                AuditResult.Failure,
                previous: null,
                next: new Dictionary<string, object?>
                {
                    ["escrowId"] = escrow.Id,
                    ["reason"] = "step-up authentication failed",
                },
                failureReason: "Step-up authentication failed.");

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Recovery-key reveal refused for {UserId}: step-up authentication failed.", actorId);

            return new RevealResult(EscrowOutcome.StepUpFailed, null,
                "That password is not correct.", 0);
        }

        if (!escrow.CanBeRevealed)
        {
            return new RevealResult(EscrowOutcome.AlreadyDeleted, null,
                "This escrow record's key has been deleted.", 0);
        }

        string plaintext;
        try
        {
            // Dispatch, not a second reveal path. Every check above this line --
            // permission, device scope, step-up password, rate limit -- has already
            // run and runs identically whichever way the key was filed. All that
            // differs here is which key opens the envelope: the symmetric master
            // key for a manually typed password, the RSA private half for one
            // sealed on an endpoint.
            plaintext = escrow.SealScheme == BitLockerSealScheme.HybridRsaV1
                ? _hybridUnsealer.Unseal(escrow.SealedRecoveryPassword)
                : _protector.Unprotect(escrow.SealedRecoveryPassword);
        }
        catch (InvalidOperationException ex)
        {
            // The hybrid private key is not configured on this host. Distinct from
            // a cryptographic failure: the record is fine and will reveal once the
            // key is provisioned, so it must not be reported as a lost key.
            _logger.LogError(ex,
                "Escrow {EscrowId} is sealed with {Scheme} but no sealing private key is configured.",
                escrow.Id, escrow.SealScheme);

            return new RevealResult(EscrowOutcome.NotFound, null,
                "This key was sealed on the endpoint and the escrow sealing private key is not "
                + "configured on this server, so it cannot be revealed here.", 0);
        }
        catch (CryptographicException ex)
        {
            // Names neither the value nor the key. The most likely cause is a
            // rotated escrow key that this row was not re-sealed under.
            _logger.LogError(ex,
                "Escrowed recovery password for {EscrowId} could not be unsealed (key version {Version}).",
                escrow.Id, escrow.KeyVersion);

            return new RevealResult(EscrowOutcome.NotFound, null,
                "The stored key could not be unsealed. It may have been sealed with a different escrow key.", 0);
        }

        escrow.RecordReveal(actorId, _timeProvider.GetUtcNow());

        StageAudit(device, actorId, actorDisplay, "bitlocker.recovery_key.revealed",
            AuditResult.Success,
            previous: null,
            next: new Dictionary<string, object?>
            {
                ["escrowId"] = escrow.Id,
                ["volumeDeviceIdentifier"] = escrow.VolumeDeviceIdentifier,
                ["keyProtectorId"] = escrow.KeyProtectorId,
                ["justification"] = justification,
                ["revealedCount"] = escrow.RevealedCount,
            });

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Recovery key for device {DeviceId} revealed by {Actor} (reveal #{Count}).",
            escrow.DeviceId, actorDisplay, escrow.RevealedCount);

        return new RevealResult(EscrowOutcome.Success, plaintext, null, 0);
    }

    // ---------------------------------------------------------------- delete

    public async Task<EscrowResult> DeleteAsync(
        Guid organizationId, Guid escrowId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var escrow = await _dbContext.BitLockerRecoveryEscrows
            .SingleOrDefaultAsync(e => e.Id == escrowId && e.OrganizationId == organizationId, cancellationToken);

        if (escrow is null)
        {
            return new EscrowResult(EscrowOutcome.NotFound, null, "No such escrow record.");
        }

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == escrow.DeviceId, cancellationToken);

        if (!escrow.TryDelete(actorId, actorDisplay, _timeProvider.GetUtcNow()))
        {
            return new EscrowResult(EscrowOutcome.AlreadyDeleted, escrow, "Already deleted.");
        }

        StageAudit(device, actorId, actorDisplay, "bitlocker.recovery_key.deleted",
            AuditResult.Success,
            previous: new Dictionary<string, object?> { ["escrowId"] = escrow.Id, ["hadKey"] = true },
            next: new Dictionary<string, object?> { ["escrowId"] = escrow.Id, ["hadKey"] = false });

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Escrowed recovery key {EscrowId} deleted by {Actor}; the ciphertext was destroyed.",
            escrow.Id, actorDisplay);

        return new EscrowResult(EscrowOutcome.Success, escrow, null);
    }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Escrow metadata for a device. Never returns the sealed value, and there is
    /// deliberately no overload that does.
    /// </summary>
    public async Task<IReadOnlyList<BitLockerRecoveryEscrow>> ListAsync(
        Guid organizationId, Guid deviceId, CancellationToken cancellationToken = default) =>
        await _dbContext.BitLockerRecoveryEscrows
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId && e.OrganizationId == organizationId && e.DeletedAt == null)
            .OrderByDescending(e => e.EscrowedAt)
            .ToListAsync(cancellationToken);

    public async Task<BitLockerRecoveryEscrow?> FindAsync(
        Guid organizationId, Guid escrowId, CancellationToken cancellationToken = default) =>
        await _dbContext.BitLockerRecoveryEscrows
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == escrowId && e.OrganizationId == organizationId, cancellationToken);

    // ----------------------------------------------------------------- audit

    /// <summary>
    /// Facts about an escrow that are safe to record: what it covers and when,
    /// never the key or the ciphertext.
    /// </summary>
    private static Dictionary<string, object?> Describe(BitLockerRecoveryEscrow escrow) => new()
    {
        ["escrowId"] = escrow.Id,
        ["volumeDeviceIdentifier"] = escrow.VolumeDeviceIdentifier,
        ["keyProtectorId"] = escrow.KeyProtectorId,
        ["driveLetter"] = escrow.DriveLetter,
        ["keyVersion"] = escrow.KeyVersion,
        ["escrowedAt"] = escrow.EscrowedAt,
    };

    /// <summary>
    /// Stages an audit entry with both state documents passed through the
    /// redactor, so nothing reaches the append-only trail unfiltered.
    /// </summary>
    private void StageAudit(
        Domain.Devices.Device? device,
        Guid actorId,
        string actorDisplay,
        string action,
        AuditResult result,
        IDictionary<string, object?>? previous,
        IDictionary<string, object?>? next,
        string? failureReason = null)
    {
        var organizationId = device?.OrganizationId ?? Guid.Empty;

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action,
            result,
            audit =>
            {
                if (device is not null)
                {
                    audit.OnDevice(device.Id, device.Hostname);
                }

                audit.OnTarget("bitlocker_recovery_escrow",
                        next?["escrowId"]?.ToString() ?? string.Empty, device?.Hostname ?? string.Empty)
                    .Requiring(Permissions.BitLocker.RecoveryKeyRead)
                    .WithStateChange(
                        previous is null ? null : AuditStateRedactor.Redact(previous),
                        next is null ? null : AuditStateRedactor.Redact(next));

                if (failureReason is not null)
                {
                    audit.WithFailureReason(failureReason);
                }
            });
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
