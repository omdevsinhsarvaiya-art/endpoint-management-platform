using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Processes agent enrollment requests. This is the gate through which an unknown
/// machine becomes a trusted device, so every refusal path is explicit and audited.
/// </summary>
public sealed class AgentEnrollmentService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<AgentEnrollmentService> logger,
    Security.IEscrowSealingKeyProvider sealingKey)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<AgentEnrollmentService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private readonly Security.IEscrowSealingKeyProvider _sealingKey = sealingKey
        ?? throw new ArgumentNullException(nameof(sealingKey));

    public async Task<EnrollmentOutcome> EnrollAsync(
        string presentedToken,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Look up by hash. A wrong token produces no row - there is nothing to
        // compare against, and the response must not reveal whether the token
        // ever existed.
        var presentedHash = SecretGenerator.HashSecret(presentedToken);

        var token = await _dbContext.EnrollmentTokens
            .SingleOrDefaultAsync(t => t.SecretHash == presentedHash, cancellationToken);

        if (token is null)
        {
            await AuditRefusalAsync(
                organizationId: null, hostname, "Unknown enrollment token.", cancellationToken);
            return EnrollmentOutcome.Refused();
        }

        var consumeResult = token.TryConsume(now);

        if (consumeResult != EnrollmentTokenConsumeResult.Consumed)
        {
            await AuditRefusalAsync(
                token.OrganizationId,
                hostname,
                $"Enrollment token refused: {consumeResult}.",
                cancellationToken,
                token);

            return EnrollmentOutcome.Refused();
        }

        // From here the token use is committed atomically with the device and
        // credential rows. The token row carries xmin optimistic concurrency, so
        // two agents racing for the last use cannot both commit.
        // Deliberately scoped to Active. A retired device is a closed record, not a
        // slot to be reoccupied: re-running the installer on a machine that was
        // retired must produce a NEW device with its own id and its own history,
        // leaving the retired row exactly as the administrator left it.
        //
        // Matching retired rows here would mean one of two bad outcomes -- either
        // the retirement is silently undone by whoever next runs the installer, or
        // (as this code did until now) the machine is refused enrolment outright and
        // can never come back without an administrator reactivating it by hand.
        // Neither is what retiring a device is supposed to mean.
        //
        // Safe because the uniqueness constraint is scoped the same way: the partial
        // unique index on (organization_id, machine_identifier) covers Active rows
        // only, so a retired row and a new active one can share a machine identifier
        // while two active ones still cannot.
        var existingDevice = await _dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.OrganizationId == token.OrganizationId
                     && d.MachineIdentifier == machineIdentifier
                     && d.Status == DeviceStatus.Active,
                cancellationToken);

        Device device;
        bool reEnrolled;

        if (existingDevice is null)
        {
            device = Device.Enroll(
                token.OrganizationId, hostname, machineIdentifier, agentVersion, operatingSystem,
                token.Id, now);
            _dbContext.Devices.Add(device);
            reEnrolled = false;
        }
        else
        {
            // Only an Active device reaches here, by construction of the query above.
            // The retired-device refusal that used to live at this point is gone: a
            // retired machine no longer matches at all, so it takes the branch above
            // and enrols as a new device rather than being turned away.
            existingDevice.ReEnroll(hostname, agentVersion, operatingSystem, token.Id, now);
            device = existingDevice;
            reEnrolled = true;

            // The machine is starting a new life; its old credential must die with
            // the old one. Revoke every active credential before issuing.
            var activeCredentials = await _dbContext.AgentCredentials
                .Where(c => c.DeviceId == device.Id && c.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var stale in activeCredentials)
            {
                stale.Revoke(now);
            }
        }

        var secret = SecretGenerator.GenerateSecret();
        var credential = new AgentCredential(
            device.Id,
            SecretGenerator.GenerateKeyId(),
            SecretGenerator.HashSecret(secret),
            now);

        // Pin the sealing key to this credential, at the one moment the device's
        // identity is being established anyway. Doing it here rather than on first
        // use is what makes trust-on-first-use unnecessary: the fingerprint is
        // bound to the same authenticated exchange that issued the credential.
        //
        // With no sealing key configured the credential is simply left unpinned,
        // and the device stays ineligible for automatic escrow. Enrollment itself
        // is never failed over it -- a machine must be able to enroll and report
        // inventory whether or not escrow is available.
        if (_sealingKey.IsConfigured)
        {
            credential.PinSealingKey(_sealingKey.Fingerprint!);
        }

        _dbContext.AgentCredentials.Add(credential);

        _auditWriter.Stage(
            token.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            hostname,
            action: reEnrolled ? "device.re_enroll" : "device.enroll",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, hostname)
                .OnTarget("enrollment_token", token.Id.ToString(), token.Name)
                .WithStateChange(null, $$"""
                    {"agentVersion":{{System.Text.Json.JsonSerializer.Serialize(agentVersion)}},"credentialKeyId":"{{credential.KeyId}}"}
                    """.Trim()));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the race for the token's remaining uses. The other enrollment
            // won; this one is refused rather than retried, because a retry would
            // re-read the token and find it exhausted anyway.
            _logger.LogWarning(
                "Enrollment for {Hostname} lost an optimistic-concurrency race on token {TokenId}; refused.",
                hostname,
                token.Id);

            _dbContext.ChangeTracker.Clear();

            await AuditRefusalAsync(
                token.OrganizationId, hostname,
                "Enrollment token exhausted (lost concurrency race).",
                cancellationToken, token);

            return EnrollmentOutcome.Refused();
        }

        _logger.LogInformation(
            "Device {DeviceId} ({Hostname}) {Action} using token {TokenId}. Credential key id {KeyId}.",
            device.Id,
            hostname,
            reEnrolled ? "re-enrolled" : "enrolled",
            token.Id,
            credential.KeyId);

        return EnrollmentOutcome.Enrolled(
            device.Id, credential.KeyId, secret, reEnrolled,
            _sealingKey.PublicKeySpki, _sealingKey.Fingerprint);
    }

    private async Task AuditRefusalAsync(
        Guid? organizationId,
        string hostname,
        string reason,
        CancellationToken cancellationToken,
        EnrollmentToken? token = null,
        Device? device = null)
    {
        // Refusals for unknown tokens have no organization; they are recorded
        // against the default organization so they remain queryable. If none
        // exists yet the platform is not seeded, and we log rather than throw -
        // refusing to enroll must never depend on audit routing succeeding.
        var targetOrganizationId = organizationId
            ?? await _dbContext.Organizations
                .OrderBy(o => o.CreatedAt)
                .Select(o => (Guid?)o.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (targetOrganizationId is null)
        {
            _logger.LogWarning(
                "Enrollment refused for {Hostname} ({Reason}), and no organization exists to audit against.",
                hostname,
                reason);
            return;
        }

        await _auditWriter.WriteImmediatelyAsync(
            targetOrganizationId.Value,
            AuditActorType.Anonymous,
            actorId: null,
            actorDisplay: hostname,
            action: "device.enroll",
            AuditResult.Denied,
            audit =>
            {
                audit.WithFailureReason(reason);
                if (token is not null)
                {
                    audit.OnTarget("enrollment_token", token.Id.ToString(), token.Name);
                }

                if (device is not null)
                {
                    audit.OnDevice(device.Id, device.Hostname);
                }
            },
            cancellationToken);

        _logger.LogWarning("Enrollment refused for {Hostname}: {Reason}", hostname, reason);
    }
}

/// <summary>
/// Result of an enrollment attempt. Refusals are deliberately reason-free: the
/// reason is audited server-side, and telling an unauthenticated caller *why*
/// (unknown vs expired vs exhausted) would let them probe the token space.
/// </summary>
public sealed record EnrollmentOutcome(
    bool Success,
    Guid DeviceId,
    string? CredentialKeyId,
    string? CredentialSecret,
    bool ReEnrolled,
    string? SealingPublicKey = null,
    string? SealingKeyFingerprint = null)
{
    public static EnrollmentOutcome Enrolled(
        Guid deviceId, string keyId, string secret, bool reEnrolled,
        string? sealingPublicKey = null, string? sealingKeyFingerprint = null) =>
        new(true, deviceId, keyId, secret, reEnrolled, sealingPublicKey, sealingKeyFingerprint);

    public static EnrollmentOutcome Refused() => new(false, Guid.Empty, null, null, false);
}
