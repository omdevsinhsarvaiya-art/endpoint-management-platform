using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EndpointPlatform.Infrastructure.BitLocker;

public enum AutomaticEscrowIngestOutcome
{
    Escrowed = 0,

    /// <summary>A live escrow already exists. Idempotent success, not an error.</summary>
    AlreadyEscrowed = 1,

    /// <summary>The credential carries no pinned fingerprint; the device must re-enroll.</summary>
    NotEligible = 2,

    /// <summary>The envelope was sealed to a key this device is not pinned to.</summary>
    FingerprintMismatch = 3,

    /// <summary>The envelope failed structural validation.</summary>
    InvalidEnvelope = 4,

    /// <summary>The endpoint has never reported a volume with that identifier.</summary>
    VolumeNotFound = 5,

    /// <summary>The volume has not reported that recovery protector.</summary>
    ProtectorNotFound = 6,
}

public sealed record AutomaticEscrowIngestResult(
    AutomaticEscrowIngestOutcome Outcome,
    Guid? EscrowId,
    string? Error)
{
    public bool Success => Outcome is AutomaticEscrowIngestOutcome.Escrowed
        or AutomaticEscrowIngestOutcome.AlreadyEscrowed;
}

/// <summary>
/// Stores an endpoint-sealed recovery envelope. Runs in the Agent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>This service holds no key and depends on nothing that does.</b> It cannot
/// open what it stores, and that is the point: the Agent API is reachable by every
/// managed endpoint, so it is the last process that should be able to read the
/// estate's recovery passwords. Compare <c>RecoveryEscrowService</c>, which handles
/// the manual path in the Admin API, takes a plaintext password and seals it there.
/// </para>
/// <para>
/// The device is never taken from the request. It comes from the authenticated
/// credential, so an agent can only escrow against itself however it words its
/// payload.
/// </para>
/// <para>
/// Every relationship is re-derived from what the endpoint previously reported
/// through inventory: the volume must belong to this device, and the protector must
/// have been seen on that volume. An agent cannot file a key against a volume it
/// does not have or a protector that does not exist, which is what stops a
/// compromised endpoint from planting records against another machine's identity.
/// </para>
/// </remarks>
public sealed class AutomaticEscrowIngestionService(
    EndpointPlatformDbContext dbContext,
    IEscrowSealingKeyProvider sealingKey,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<AutomaticEscrowIngestionService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IEscrowSealingKeyProvider _sealingKey = sealingKey
        ?? throw new ArgumentNullException(nameof(sealingKey));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<AutomaticEscrowIngestionService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<AutomaticEscrowIngestResult> IngestAsync(
        Device device,
        AgentCredential credential,
        string volumeDeviceIdentifier,
        string keyProtectorId,
        string sealedEnvelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(credential);

        // ---- eligibility ---------------------------------------------------
        // Devices enrolled before automatic escrow have no pinned fingerprint and
        // are refused here as well as on the endpoint. Two independent checks,
        // because the agent-side one protects the password from being read and
        // this one protects the table from being written.
        if (!credential.IsAutomaticEscrowEligible)
        {
            await FailAsync(device, volumeDeviceIdentifier, keyProtectorId,
                BitLockerEscrowFailureCategory.NotEligible, cancellationToken);

            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.NotEligible, null,
                "This device is not eligible for automatic escrow. It must re-enroll to establish a "
                + "pinned sealing key.");
        }

        // ---- envelope structure --------------------------------------------
        var structural = SealedRecoveryEnvelope.Validate(sealedEnvelope, out var envelope);

        if (structural != SealedEnvelopeError.None || envelope is null)
        {
            await FailAsync(device, volumeDeviceIdentifier, keyProtectorId,
                BitLockerEscrowFailureCategory.SealingFailed, cancellationToken);

            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.InvalidEnvelope, null,
                SealedRecoveryEnvelope.Describe(structural));
        }

        // ---- the pin --------------------------------------------------------
        // Checked against the credential AND against the key currently configured.
        // The first stops a device filing under a key it was never pinned to; the
        // second stops an envelope sealed to a retired key being accepted after a
        // rotation, when nothing here could ever unwrap it again.
        if (!FingerprintMatches(envelope.KeyFingerprint, credential.SealingKeyFingerprint)
            || (_sealingKey.IsConfigured
                && !FingerprintMatches(envelope.KeyFingerprint, _sealingKey.Fingerprint)))
        {
            _logger.LogWarning(
                "Automatic escrow refused for device {DeviceId}: the envelope was sealed to a key "
                + "this device is not pinned to.", device.Id);

            await FailAsync(device, volumeDeviceIdentifier, keyProtectorId,
                BitLockerEscrowFailureCategory.FingerprintMismatch, cancellationToken);

            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.FingerprintMismatch, null,
                "The envelope was sealed to a key that does not match this device's pinned sealing key.");
        }

        // ---- the volume and protector must be ones this device reported ------
        var volume = await _dbContext.DeviceBitLockerVolumes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                v => v.DeviceId == device.Id && v.DeviceIdentifier == volumeDeviceIdentifier,
                cancellationToken);

        if (volume is null)
        {
            await FailAsync(device, volumeDeviceIdentifier, keyProtectorId,
                BitLockerEscrowFailureCategory.ProtectorGone, cancellationToken);

            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.VolumeNotFound, null,
                "This endpoint has not reported a volume with that identifier.");
        }

        if (!ProtectorWasReported(volume, keyProtectorId))
        {
            await FailAsync(device, volumeDeviceIdentifier, keyProtectorId,
                BitLockerEscrowFailureCategory.ProtectorGone, cancellationToken);

            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.ProtectorNotFound, null,
                "This volume has not reported that recovery protector.");
        }

        // ---- idempotence -----------------------------------------------------
        var normalisedProtector = NormaliseProtector(keyProtectorId);

        var existing = await _dbContext.BitLockerRecoveryEscrows
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.DeviceId == device.Id
                    && e.VolumeDeviceIdentifier == volumeDeviceIdentifier
                    && e.KeyProtectorId == normalisedProtector
                    && e.IsActive,
                cancellationToken);

        if (existing is not null)
        {
            // Success, not a conflict. Repeated inventory must be free, and the
            // agent uses this answer to stop reading the password at all.
            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.AlreadyEscrowed, existing.Id, null);
        }

        var now = _timeProvider.GetUtcNow();

        var escrow = BitLockerRecoveryEscrow.Automatic(
            device.OrganizationId,
            device.Id,
            volumeDeviceIdentifier,
            keyProtectorId,
            volume.DriveLetter,
            sealedEnvelope,
            keyVersion: 1,
            agentDisplay: $"{device.Hostname} (agent)",
            now);

        _dbContext.BitLockerRecoveryEscrows.Add(escrow);

        await RecordAttemptAsync(device, volumeDeviceIdentifier, keyProtectorId, success: true, now,
            BitLockerEscrowFailureCategory.None, cancellationToken);

        StageAudit(device, "bitlocker.recovery_key.auto_escrowed", AuditResult.Success,
            volumeDeviceIdentifier, keyProtectorId, escrow.Id, null);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Two uploads for the same protector raced. The partial unique index is
            // the authority, and losing that race is the idempotent outcome rather
            // than an error -- the key is filed either way.
            _dbContext.ChangeTracker.Clear();

            var winner = await _dbContext.BitLockerRecoveryEscrows
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    e => e.DeviceId == device.Id
                        && e.VolumeDeviceIdentifier == volumeDeviceIdentifier
                        && e.KeyProtectorId == normalisedProtector
                        && e.IsActive,
                    cancellationToken);

            return new AutomaticEscrowIngestResult(
                AutomaticEscrowIngestOutcome.AlreadyEscrowed, winner?.Id, null);
        }

        _logger.LogInformation(
            "Recovery key automatically escrowed for device {DeviceId} volume {Volume} protector "
            + "{Protector}.", device.Id, volumeDeviceIdentifier, escrow.KeyProtectorId);

        return new AutomaticEscrowIngestResult(AutomaticEscrowIngestOutcome.Escrowed, escrow.Id, null);
    }

    /// <summary>
    /// Escrow state for every recovery protector this device has reported.
    /// </summary>
    /// <remarks>
    /// Metadata only. Nothing in the projection touches the sealed envelope column,
    /// so there is no path by which ciphertext could reach an agent.
    /// </remarks>
    /// <summary>
    /// Escrow and retry state for every recovery protector this device has reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Metadata only. Nothing in the projection touches the sealed envelope column,
    /// so there is no path by which ciphertext could reach an agent.
    /// </para>
    /// <para>
    /// <b>Whether an attempt is due is decided here, on the server.</b> The agent is
    /// told yes or no and does not compute it, which is what stops a restarting
    /// agent from resetting its own backoff. A protector with no attempt row has
    /// never been tried and is due immediately.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<EscrowProtectorStatus>> GetStatusAsync(
        Guid deviceId, CancellationToken cancellationToken = default)
    {
        var volumes = await _dbContext.DeviceBitLockerVolumes
            .AsNoTracking()
            .Where(v => v.DeviceId == deviceId)
            .Select(v => new { v.DeviceIdentifier, v.RecoveryProtectorIds })
            .ToListAsync(cancellationToken);

        var escrows = await _dbContext.BitLockerRecoveryEscrows
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId && e.IsActive)
            .Select(e => new { e.VolumeDeviceIdentifier, e.KeyProtectorId, e.EscrowedAt })
            .ToListAsync(cancellationToken);

        var attempts = await _dbContext.BitLockerEscrowAttempts
            .AsNoTracking()
            .Where(a => a.DeviceId == deviceId)
            .ToListAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var results = new List<EscrowProtectorStatus>();

        foreach (var volume in volumes)
        {
            foreach (var protector in SplitProtectors(volume.RecoveryProtectorIds))
            {
                var normalised = NormaliseProtector(protector);

                var escrow = escrows.FirstOrDefault(
                    e => e.VolumeDeviceIdentifier == volume.DeviceIdentifier
                        && e.KeyProtectorId == normalised);

                var attempt = attempts.FirstOrDefault(
                    a => a.VolumeDeviceIdentifier == volume.DeviceIdentifier
                        && a.KeyProtectorId == normalised);

                // No attempt row means this protector has never been tried. A
                // protector that appears after a rotation lands here, gets its own
                // row on first attempt, and carries none of the previous one's
                // failure history.
                var state = attempt?.State ?? BitLockerEscrowAttemptState.Pending;
                var due = attempt?.IsDue(now) ?? true;

                results.Add(new EscrowProtectorStatus(
                    volume.DeviceIdentifier,
                    protector,
                    escrow is not null,
                    escrow?.EscrowedAt,
                    state.ToString(),
                    // An escrowed protector is never due, whatever the attempt row
                    // says: there is nothing left to collect.
                    escrow is null && due,
                    attempt?.NextAttemptAt));
            }
        }

        return results;
    }

    // ------------------------------------------------------------------ helpers

    private static bool FingerprintMatches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormaliseProtector(string value) =>
        Guid.TryParse(value.Trim().Trim('{', '}'), out var parsed)
            ? parsed.ToString("D")
            : value.Trim();

    private static IEnumerable<string> SplitProtectors(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ProtectorWasReported(DeviceBitLockerVolume volume, string keyProtectorId)
    {
        var wanted = NormaliseProtector(keyProtectorId);

        return SplitProtectors(volume.RecoveryProtectorIds)
            .Any(reported => NormaliseProtector(reported) == wanted);
    }

    private async Task FailAsync(
        Device device,
        string volume,
        string protector,
        BitLockerEscrowFailureCategory category,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        await RecordAttemptAsync(device, volume, protector, success: false, now, category, cancellationToken);

        StageAudit(device, "bitlocker.recovery_key.auto_escrow_failed", AuditResult.Failure,
            volume, protector, null, category);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records the attempt so the retry schedule advances. The scheduler that reads
    /// this is a later phase; the state it needs is written from the start so no
    /// attempt goes unrecorded in the meantime.
    /// </summary>
    private async Task RecordAttemptAsync(
        Device device,
        string volume,
        string protector,
        bool success,
        DateTimeOffset now,
        BitLockerEscrowFailureCategory category,
        CancellationToken cancellationToken)
    {
        var normalised = NormaliseProtector(protector);

        var attempt = await _dbContext.BitLockerEscrowAttempts
            .SingleOrDefaultAsync(
                a => a.DeviceId == device.Id
                    && a.VolumeDeviceIdentifier == volume
                    && a.KeyProtectorId == normalised,
                cancellationToken);

        if (attempt is null)
        {
            attempt = new BitLockerEscrowAttempt(
                device.OrganizationId, device.Id, volume, normalised, now);

            _dbContext.BitLockerEscrowAttempts.Add(attempt);
        }

        if (success)
        {
            attempt.RecordSuccess(now);
        }
        else
        {
            attempt.RecordFailure(category, now);
        }
    }

    /// <summary>
    /// Audits the operation with identifiers and a category, never a value.
    /// </summary>
    /// <remarks>
    /// The state document is built from fields that cannot hold key material -- ids,
    /// a timestamp, an enum -- and still goes through <see cref="AuditStateRedactor"/>
    /// like every other audited state on this feature, so a field added later cannot
    /// quietly bypass it.
    /// </remarks>
    private void StageAudit(
        Device device,
        string action,
        AuditResult result,
        string volume,
        string protector,
        Guid? escrowId,
        BitLockerEscrowFailureCategory? category)
    {
        _auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            device.Hostname,
            action,
            result,
            audit =>
            {
                audit.OnDevice(device.Id, device.Hostname);

                audit.OnTarget("bitlocker_recovery_escrow", escrowId?.ToString() ?? string.Empty, device.Hostname)
                    .WithStateChange(null, AuditStateRedactor.Redact(new Dictionary<string, object?>
                    {
                        ["escrowId"] = escrowId,
                        ["volumeDeviceIdentifier"] = volume,
                        ["keyProtectorId"] = NormaliseProtector(protector),
                        ["origin"] = nameof(BitLockerEscrowOrigin.Automatic),
                        ["sealScheme"] = BitLockerSealScheme.HybridRsaV1,
                        ["failureCategory"] = category?.ToString(),
                    }));

                if (category is not null and not BitLockerEscrowFailureCategory.None)
                {
                    audit.WithFailureReason(category.Value.ToString());
                }
            });
    }
}

/// <summary>One protector's escrow and retry position, as reported to an agent.</summary>
public sealed record EscrowProtectorStatus(
    string Volume,
    string Protector,
    bool Escrowed,
    DateTimeOffset? EscrowedAt,
    string State,
    bool Due,
    DateTimeOffset? NextAttemptAt);
