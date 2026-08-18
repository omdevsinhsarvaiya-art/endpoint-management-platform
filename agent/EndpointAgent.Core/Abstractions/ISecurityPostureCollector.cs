using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads the machine's security posture (Defender, firewall, Secure Boot, TPM,
/// BitLocker, local-admin count).
/// </summary>
/// <remarks>
/// Read-only. Each fact is independently fault-isolated: items that require
/// elevation the agent lacks are reported as null ("unknown"), never as a false
/// negative that would make an endpoint look non-compliant when it is not.
/// </remarks>
public interface ISecurityPostureCollector
{
    ValueTask<InventorySecurityPosture> CollectAsync(CancellationToken cancellationToken = default);
}
