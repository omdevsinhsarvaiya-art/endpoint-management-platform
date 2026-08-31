using Microsoft.Extensions.Options;
using EndpointAgent.Core.Configuration;
using System.Security.Cryptography;
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
    IEnrollmentStateStore enrollmentStateStore,
    ISystemInfoProvider systemInfoProvider,
    IOptions<AgentOptions> agentOptions,
    ILogger<AgentEnrollmentManager> logger)
{
    private readonly IEnrollmentStateStore _enrollmentStateStore = enrollmentStateStore
        ?? throw new ArgumentNullException(nameof(enrollmentStateStore));

    private readonly string _serverBaseUrl = agentOptions?.Value.ServerBaseUrl
        ?? throw new ArgumentNullException(nameof(agentOptions));

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

        // No credential and no token: the normal path for an MSI-installed agent.
        // Ask to be managed and wait for an administrator, rather than requiring
        // somebody to type a token on the endpoint.
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            return await RunApprovalEnrollmentAsync(agentVersion, cancellationToken);
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

        // The sealing-key fingerprint is pinned here, at the one moment the
        // server's identity has been authenticated, and is stored with the
        // credential. Omitting it leaves the credential ineligible for automatic
        // escrow no matter what the server believes -- the agent's first gate
        // reads this value, not the server's.
        var credential = new DeviceCredential(
            response.DeviceId,
            response.CredentialKeyId,
            response.CredentialSecret,
            response.SealingKeyFingerprint);

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
    /// <summary>
    /// Drives the approval-gated enrollment protocol when no credential exists and no
    /// enrollment token is configured — the normal path for an MSI-installed agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restart-safe by construction. The request secret is persisted BEFORE the
    /// request is sent, so a service restart or reboot at any point resumes the same
    /// request instead of orphaning one an administrator is already looking at. Only
    /// when a request is genuinely gone — rejected, or expired past the server's
    /// retention — does the agent start a new one.
    /// </para>
    /// <para>
    /// One attempt per call. The caller owns the loop and the backoff, so this method
    /// never blocks the service on a network that is not there.
    /// </para>
    /// </remarks>
    private async Task<DeviceCredential?> RunApprovalEnrollmentAsync(
        string agentVersion,
        CancellationToken cancellationToken)
    {
        var machineIdentifier = await _systemInfoProvider.GetMachineIdentifierAsync(cancellationToken);
        var state = await _enrollmentStateStore.LoadAsync(cancellationToken);

        // A state file carried over from another machine, or from before the server
        // was repointed, describes a request this agent cannot claim. Start fresh
        // rather than poll something that will never resolve.
        if (state is not null && !string.Equals(state.MachineIdentifier, machineIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("The stored enrollment request belongs to a different machine; starting a new one.");
            await _enrollmentStateStore.ClearAsync(cancellationToken);
            state = null;
        }

        if (state is null)
        {
            state = await SubmitEnrollmentRequestAsync(machineIdentifier, agentVersion, cancellationToken);
            if (state is null)
            {
                return null; // transient; the caller backs off and retries
            }
        }

        return await TryClaimAsync(state, cancellationToken);
    }

    /// <summary>
    /// Generates the proof secret, persists it, and registers the request.
    /// </summary>
    /// <remarks>
    /// Order matters: the secret is written to protected storage BEFORE the request is
    /// sent. If the process dies between the two, the agent retries with the same
    /// secret and the server treats it as the same request. The reverse order could
    /// register a request whose secret no longer exists anywhere, leaving an
    /// unclaimable entry for an administrator to approve.
    /// </remarks>
    private async Task<PendingEnrollmentState?> SubmitEnrollmentRequestAsync(
        string machineIdentifier,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        // 256 bits from a CSPRNG. Only its hash is ever transmitted here.
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var requestSecret = Convert.ToBase64String(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);

        var requestId = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestSecret)));

        var hostname = Environment.MachineName;
        var operatingSystem = await _systemInfoProvider.GetOperatingSystemDescriptionAsync(cancellationToken);

        var state = new PendingEnrollmentState(
            requestSecret, requestId, _serverBaseUrl, machineIdentifier, DateTimeOffset.UtcNow);

        await _enrollmentStateStore.SaveAsync(state, cancellationToken);

        var result = await _apiClient.RequestEnrollmentAsync(
            new EnrollmentRequestRequest(requestId, machineIdentifier, hostname, agentVersion, operatingSystem),
            cancellationToken);

        if (result.Status != AgentApiStatus.Success)
        {
            // Keep the saved state: the request id is derived from the secret, so
            // retrying sends the identical request rather than creating a second one.
            _logger.LogWarning(
                "Enrollment request could not be submitted ({Status}); will retry.", result.Status);
            return null;
        }

        _logger.LogInformation(
            "Enrollment request submitted for {Hostname}; awaiting administrator approval.", hostname);

        return state;
    }

    /// <summary>
    /// Proves possession and, once approved, stores the issued credential.
    /// </summary>
    private async Task<DeviceCredential?> TryClaimAsync(
        PendingEnrollmentState state,
        CancellationToken cancellationToken)
    {
        var result = await _apiClient.ClaimEnrollmentAsync(
            new EnrollmentClaimRequest(state.RequestSecret), cancellationToken);

        if (result.Status == AgentApiStatus.Rejected)
        {
            // 403: unknown, already claimed, or expired. The request is dead; drop it
            // so the next attempt starts a new one rather than polling a ghost.
            _logger.LogWarning("The enrollment request is no longer valid; a new request will be made.");
            await _enrollmentStateStore.ClearAsync(cancellationToken);
            return null;
        }

        if (result.Status != AgentApiStatus.Success || result.Value is null)
        {
            _logger.LogDebug("Enrollment claim did not complete ({Status}); will retry.", result.Status);
            return null;
        }

        switch (result.Value.Status)
        {
            case "pending":
                _logger.LogInformation("Enrollment is pending administrator approval.");
                return null;

            case "rejected":
                _logger.LogWarning(
                    "Enrollment was rejected by an administrator. This machine will not be managed "
                    + "until a new request is approved.");
                await _enrollmentStateStore.ClearAsync(cancellationToken);
                return null;

            case "approved":
                break;

            default:
                _logger.LogWarning("Unrecognised enrollment status from the server; will retry.");
                return null;
        }

        var response = result.Value;
        if (response.DeviceId is null
            || string.IsNullOrWhiteSpace(response.CredentialKeyId)
            || string.IsNullOrWhiteSpace(response.CredentialSecret))
        {
            _logger.LogError("The server approved enrollment but returned an incomplete credential.");
            return null;
        }

        // Same pinning as the direct-token path above. A device approved through
        // the request/claim flow must end up in exactly the same state as one
        // enrolled with a token, or approval-enrolled machines would silently
        // never be eligible.
        var credential = new DeviceCredential(
            response.DeviceId.Value,
            response.CredentialKeyId,
            response.CredentialSecret,
            response.SealingKeyFingerprint);

        try
        {
            await _credentialStore.SaveAsync(credential, cancellationToken);
        }
        catch (Exception ex)
        {
            // The credential was issued and consumed the request; it exists only here.
            // Reporting success would leave a device the server believes is enrolled
            // and an agent that cannot authenticate, so fail loudly and keep the
            // pending state so the operator can see the machine is stuck.
            _logger.LogError(
                ex,
                "Enrollment succeeded but the device credential could not be stored. "
                + "The agent cannot authenticate; this must be investigated.");
            return null;
        }

        // Only now is the request finished with. Clearing earlier would lose the
        // ability to diagnose a failed credential write.
        await _enrollmentStateStore.ClearAsync(cancellationToken);

        _logger.LogInformation(
            "Enrollment complete. Device id: {DeviceId} ({Kind}).",
            response.DeviceId, response.ReEnrolled ? "re-enrolled" : "new device");

        return credential;
    }

}
