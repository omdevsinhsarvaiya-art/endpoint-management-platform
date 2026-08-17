using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Validates a presented device credential and resolves the device behind it.
/// </summary>
/// <remarks>
/// <para>
/// The credential travels as <c>keyId.secret</c> in the
/// <c>X-Agent-Credential</c> header. Validation: parse, look up by key id,
/// constant-time-compare the secret's hash, then check credential revocation and
/// device status. Every refusal returns the same generic result to the caller;
/// the distinguishing detail goes to the log and (for suspicious cases) the audit
/// trail, not to the wire.
/// </para>
/// <para>
/// A retired device presenting a valid-shaped credential is logged at Warning:
/// that combination means someone is replaying a credential that should have
/// been destroyed.
/// </para>
/// </remarks>
public sealed class AgentAuthenticationService(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<AgentAuthenticationService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<AgentAuthenticationService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<AgentAuthenticationResult> AuthenticateAsync(
        string? credentialHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialHeader))
        {
            return AgentAuthenticationResult.Failed;
        }

        // Shape: 32 hex chars, '.', 64 hex chars.
        var separator = credentialHeader.IndexOf('.', StringComparison.Ordinal);

        if (separator != Domain.Enrollment.AgentCredential.KeyIdLength
            || credentialHeader.Length != Domain.Enrollment.AgentCredential.KeyIdLength + 1 + 64)
        {
            return AgentAuthenticationResult.Failed;
        }

        var keyId = credentialHeader[..separator];
        var secret = credentialHeader[(separator + 1)..];

        var credential = await _dbContext.AgentCredentials
            .SingleOrDefaultAsync(c => c.KeyId == keyId, cancellationToken);

        if (credential is null)
        {
            _logger.LogWarning("Agent authentication failed: unknown credential key id presented.");
            return AgentAuthenticationResult.Failed;
        }

        if (!SecretGenerator.HashesEqual(credential.SecretHash, SecretGenerator.HashSecret(secret)))
        {
            _logger.LogWarning(
                "Agent authentication failed: wrong secret for key id {KeyId} (device {DeviceId}).",
                keyId,
                credential.DeviceId);
            return AgentAuthenticationResult.Failed;
        }

        if (!credential.IsActive)
        {
            _logger.LogWarning(
                "Agent authentication refused: REVOKED credential {KeyId} presented for device {DeviceId}. " +
                "This may indicate credential replay.",
                keyId,
                credential.DeviceId);
            return AgentAuthenticationResult.Failed;
        }

        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(d => d.Id == credential.DeviceId, cancellationToken);

        if (device is null)
        {
            _logger.LogError(
                "Agent credential {KeyId} references missing device {DeviceId}; data integrity issue.",
                keyId,
                credential.DeviceId);
            return AgentAuthenticationResult.Failed;
        }

        if (device.Status != DeviceStatus.Active)
        {
            _logger.LogWarning(
                "Agent authentication refused: credential {KeyId} presented for {Status} device {DeviceId}. " +
                "A retired device should not be calling in.",
                keyId,
                device.Status,
                device.Id);
            return AgentAuthenticationResult.Failed;
        }

        credential.RecordUse(_timeProvider.GetUtcNow());
        // LastUsedAt persists with the endpoint's own SaveChanges (heartbeat etc.);
        // an authentication that leads to no write is not worth a dedicated commit.

        return AgentAuthenticationResult.Authenticated(device, credential.Id);
    }
}

public sealed record AgentAuthenticationResult(bool Success, Device? Device, Guid CredentialId)
{
    public static readonly AgentAuthenticationResult Failed = new(false, null, Guid.Empty);

    public static AgentAuthenticationResult Authenticated(Device device, Guid credentialId) =>
        new(true, device, credentialId);
}
