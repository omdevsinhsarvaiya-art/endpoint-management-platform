using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Communication;

/// <summary>
/// Redeems one-time secret references from the Agent API.
/// </summary>
/// <remarks>
/// A single attempt by design. The reference is consumed server-side on read, so a
/// retry could only ever fail; retrying would also widen the window in which a
/// plaintext exists. On any failure the caller fails the task and the operator
/// re-issues it with a fresh secret.
/// </remarks>
public sealed class ServerSecretRedeemer(
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    ILogger<ServerSecretRedeemer> logger) : ISecretRedeemer
{
    private readonly IAgentApiClient _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    private readonly IDeviceCredentialStore _credentialStore = credentialStore
        ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly ILogger<ServerSecretRedeemer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<string?> RedeemAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            _logger.LogWarning("Cannot redeem a secret without a device credential.");
            return null;
        }

        var result = await _apiClient.RedeemSecretAsync(secretReference, credential, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            // Never log the reference or any part of the secret.
            _logger.LogWarning("Secret redemption was not successful ({Status}).", result.Status);
            return null;
        }

        return result.Value.Secret;
    }
}
