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
    ILogger<AgentEnrollmentService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<AgentEnrollmentService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

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
        var existingDevice = await _dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.OrganizationId == token.OrganizationId && d.MachineIdentifier == machineIdentifier,
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
            if (existingDevice.IsRetired)
            {
                await AuditRefusalAsync(
                    token.OrganizationId,
                    hostname,
                    "Machine matches a retired device; re-enrollment requires administrator reactivation.",
                    cancellationToken,
                    token,
                    existingDevice);

                return EnrollmentOutcome.Refused();
            }

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

        return EnrollmentOutcome.Enrolled(device.Id, credential.KeyId, secret, reEnrolled);
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
    bool ReEnrolled)
{
    public static EnrollmentOutcome Enrolled(Guid deviceId, string keyId, string secret, bool reEnrolled) =>
        new(true, deviceId, keyId, secret, reEnrolled);

    public static EnrollmentOutcome Refused() => new(false, Guid.Empty, null, null, false);
}
