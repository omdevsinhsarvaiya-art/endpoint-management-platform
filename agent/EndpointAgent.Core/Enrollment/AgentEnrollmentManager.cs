using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Enrollment;

/// <summary>
/// Establishes and maintains this machine's device identity.
/// </summary>
/// <remarks>
/// Platform-neutral by design: everything Windows-specific (credential storage,
/// machine facts) arrives through the abstractions, so the whole state machine is
/// unit-tested with fakes in <c>EndpointAgent.Core.Tests</c>.
/// </remarks>
public sealed class AgentEnrollmentManager(
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    ISystemInfoProvider systemInfoProvider,
    ILogger<AgentEnrollmentManager> logger)
{
    private readonly IAgentApiClient _apiClient = apiClient
        ?? throw new ArgumentNullException(nameof(apiClient));

    private readonly IDeviceCredentialStore _credentialStore = credentialStore
        ?? throw new ArgumentNullException(nameof(credentialStore));

    private readonly ISystemInfoProvider _systemInfoProvider = systemInfoProvider
        ?? throw new ArgumentNullException(nameof(systemInfoProvider));

    private readonly ILogger<AgentEnrollmentManager> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Returns the stored credential, or enrolls with the provided token when the
    /// machine has none. Returns null when the machine has no credential and no
    /// (working) token — the caller backs off and retries later.
    /// </summary>
    public async Task<DeviceCredential?> EnsureEnrolledAsync(
        string? enrollmentToken,
        string agentVersion,
        CancellationToken cancellationToken = default)
    {
        var existing = await _credentialStore.LoadAsync(cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            _logger.LogWarning(
                "This machine has no device credential and no enrollment token is configured. "
                + "Provide one via ENDPOINTAGENT_Enrollment__Token and restart the service.");
            return null;
        }

        _logger.LogInformation("No device credential found; attempting enrollment.");

        var request = new EnrollRequest(
            enrollmentToken.Trim(),
            _systemInfoProvider.GetHostName(),
            await _systemInfoProvider.GetMachineIdentifierAsync(cancellationToken),
            agentVersion,
            await _systemInfoProvider.GetOperatingSystemDescriptionAsync(cancellationToken));

        var result = await _apiClient.EnrollAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            // 403 here covers unknown/expired/revoked/exhausted uniformly - the
            // server tells us nothing more, on purpose.
            _logger.LogError(
                "Enrollment was not accepted (status: {Status}). The token may be expired, revoked, "
                + "exhausted or wrong. A new token is required.",
                result.Status);
            return null;
        }

        var response = result.Value!;

        var credential = new DeviceCredential(
            response.DeviceId,
            response.CredentialKeyId,
            response.CredentialSecret);

        // Persist before returning: if the process dies between here and first
        // heartbeat, the identity must survive.
        await _credentialStore.SaveAsync(credential, cancellationToken);

        _logger.LogInformation(
            "Enrollment {Kind} succeeded. Device id: {DeviceId}, credential key id: {KeyId}.",
            response.ReEnrolled ? "(re-enrollment)" : "(new device)",
            response.DeviceId,
            response.CredentialKeyId);

        return credential;
    }

    /// <summary>
    /// Discards the stored credential after the server refused it (401). The next
    /// <see cref="EnsureEnrolledAsync"/> either re-enrolls (token available) or
    /// parks the agent with a clear operator-facing log message.
    /// </summary>
    public async Task DiscardRejectedCredentialAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "The server no longer accepts this machine's device credential; discarding it. "
            + "Re-enrollment requires a valid enrollment token.");

        await _credentialStore.ClearAsync(cancellationToken);
    }
}
