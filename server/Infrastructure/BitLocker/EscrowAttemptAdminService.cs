using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.BitLocker;

/// <summary>One protector's automatic-escrow position, for the console.</summary>
/// <remarks>
/// Metadata only. There is no field here that could carry an envelope, a
/// ciphertext or a password, which is what makes it safe to render.
/// </remarks>
public sealed record EscrowAttemptView(
    Guid Id,
    Guid DeviceId,
    string VolumeDeviceIdentifier,
    string KeyProtectorId,
    string State,
    int AttemptCount,
    int MaxAttempts,
    string LastFailure,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? EscrowedAt);

/// <summary>
/// A device's automatic-escrow position, for the console.
/// </summary>
/// <param name="Eligible">
/// Whether the device's active credential carries a pinned sealing key. False
/// means it must re-enroll; it does <em>not</em> mean collection has failed.
/// </param>
/// <param name="SealingKeyFingerprint">
/// The key this device is pinned to. Public material -- it names a key and
/// decrypts nothing -- and useful when diagnosing a mismatch after a rotation.
/// </param>
public sealed record AutomaticEscrowStatus(
    bool Eligible,
    string? SealingKeyFingerprint,
    IReadOnlyList<EscrowAttemptView> Attempts);

public enum EscrowResetOutcome
{
    Reset = 0,
    NotFound = 1,

    /// <summary>Nothing to re-arm: this protector is not in a stopped state.</summary>
    NotExhausted = 2,
}

/// <summary>
/// Administrative operations over automatic-escrow retry state.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>RecoveryEscrowService</c> because nothing here touches key
/// material: it reads and re-arms a schedule. Keeping it apart means the type that
/// can decrypt and the type an operator uses to unstick a machine are not the same
/// type, and a mistake in one cannot reach the other.
/// </para>
/// <para>
/// Reset is deliberately an administrator action rather than anything automatic. A
/// protector exhausts its attempts because something on that machine needs
/// attention -- elevation, policy, a volume mid-conversion -- and re-arming it on a
/// timer would bury the signal instead of surfacing it.
/// </para>
/// </remarks>
public sealed class EscrowAttemptAdminService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<EscrowAttemptAdminService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<EscrowAttemptAdminService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Whether a device may collect recovery keys automatically, and how far each
    /// of its protectors has got.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eligibility comes from the device's active credential, not from whether
    /// attempts exist.</b> Inferring it from attempt rows was wrong in a way that
    /// pointed operators at the wrong problem: a properly pinned device that the
    /// agent has simply not reached yet has no attempt row, and reading that as
    /// "re-enrollment required" would send somebody to re-enroll a machine that
    /// needs nothing done to it.
    /// </para>
    /// <para>
    /// The two states are genuinely different. No pinned fingerprint means the
    /// device <em>cannot</em> participate until it re-enrolls. No attempt row means
    /// it can and has not yet -- which resolves itself on the next heartbeat.
    /// </para>
    /// </remarks>
    public async Task<AutomaticEscrowStatus> GetStatusAsync(
        Guid organizationId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        // The active credential is the authority. Revoking one withdraws
        // eligibility with it, which is why this filters on RevokedAt rather than
        // taking the most recent row.
        var credential = await _dbContext.AgentCredentials
            .AsNoTracking()
            .Where(c => c.DeviceId == deviceId && c.RevokedAt == null)
            .OrderByDescending(c => c.IssuedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new AutomaticEscrowStatus(
            credential?.IsAutomaticEscrowEligible ?? false,
            credential?.SealingKeyFingerprint,
            await ListAsync(organizationId, deviceId, cancellationToken));
    }

    /// <summary>Automatic-escrow state for one device's protectors.</summary>
    public async Task<IReadOnlyList<EscrowAttemptView>> ListAsync(
        Guid organizationId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.BitLockerEscrowAttempts
            .AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.DeviceId == deviceId)
            .OrderBy(a => a.VolumeDeviceIdentifier).ThenBy(a => a.KeyProtectorId)
            .Select(a => new EscrowAttemptView(
                a.Id,
                a.DeviceId,
                a.VolumeDeviceIdentifier,
                a.KeyProtectorId,
                a.State.ToString(),
                a.AttemptCount,
                BitLockerEscrowAttempt.MaxAttempts,
                a.LastFailure.ToString(),
                a.NextAttemptAt,
                a.LastAttemptAt,
                a.EscrowedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Finds an attempt within the caller's organization, for scope checking.</summary>
    public Task<BitLockerEscrowAttempt?> FindAsync(
        Guid organizationId, Guid attemptId, CancellationToken cancellationToken = default) =>
        _dbContext.BitLockerEscrowAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                a => a.Id == attemptId && a.OrganizationId == organizationId, cancellationToken);

    /// <summary>
    /// Re-arms one exhausted protector so automatic escrow may try again.
    /// </summary>
    /// <remarks>
    /// Scoped to a single attempt row -- one device, one volume, one protector.
    /// There is deliberately no bulk reset: an operator re-arming an estate at once
    /// would be doing so without having looked at why any of it stopped.
    /// </remarks>
    public async Task<EscrowResetOutcome> ResetAsync(
        Guid organizationId,
        Guid attemptId,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var attempt = await _dbContext.BitLockerEscrowAttempts
            .SingleOrDefaultAsync(
                a => a.Id == attemptId && a.OrganizationId == organizationId, cancellationToken);

        if (attempt is null)
        {
            return EscrowResetOutcome.NotFound;
        }

        // Only a stopped protector has anything to re-arm. Resetting one that is
        // mid-schedule would silently hand it extra attempts.
        if (attempt.State != BitLockerEscrowAttemptState.RetryExhausted
            && attempt.State != BitLockerEscrowAttemptState.Failed)
        {
            return EscrowResetOutcome.NotExhausted;
        }

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == attempt.DeviceId, cancellationToken);

        var previousState = attempt.State.ToString();
        var previousCount = attempt.AttemptCount;

        attempt.Reset(actorId, _timeProvider.GetUtcNow());

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            "bitlocker.recovery_key.auto_escrow_reset",
            AuditResult.Success,
            audit =>
            {
                if (device is not null)
                {
                    audit.OnDevice(device.Id, device.Hostname);
                }

                audit.OnTarget("bitlocker_escrow_attempt", attempt.Id.ToString(), device?.Hostname ?? string.Empty)
                    .Requiring(Domain.Authorization.Permissions.BitLocker.RecoveryKeyManage)
                    .WithStateChange(
                        AuditStateRedactor.Redact(new Dictionary<string, object?>
                        {
                            ["state"] = previousState,
                            ["attemptCount"] = previousCount,
                        }),
                        AuditStateRedactor.Redact(new Dictionary<string, object?>
                        {
                            ["state"] = attempt.State.ToString(),
                            ["attemptCount"] = attempt.AttemptCount,
                            ["volumeDeviceIdentifier"] = attempt.VolumeDeviceIdentifier,
                            ["keyProtectorId"] = attempt.KeyProtectorId,
                        }));
            });

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Automatic escrow re-armed for device {DeviceId} protector {Protector} by {Actor}.",
            attempt.DeviceId, attempt.KeyProtectorId, actorDisplay);

        return EscrowResetOutcome.Reset;
    }
}
