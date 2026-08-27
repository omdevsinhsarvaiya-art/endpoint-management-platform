using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads BitLocker volume encryption state.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a second face on the <em>same</em> implementation as
/// <see cref="ISecurityPostureCollector"/> rather than a second BitLocker
/// component. There is one place in the agent that talks to
/// <c>Win32_EncryptableVolume</c>, and the single-field system-drive status the
/// posture already reports is derived from the same read as the per-volume detail.
/// Two independent readers could disagree about the same machine, and the first
/// anyone would notice is a compliance score contradicting a volume list.
/// </para>
/// <para>
/// Read-only, and structurally so: this interface exposes no way to encrypt,
/// decrypt, suspend, resume, or fetch a key. Encryption operations are a separate,
/// task-gated capability with its own abstraction, so nothing that merely collects
/// inventory can be widened into a mutation by accident.
/// </para>
/// <para>
/// <b>No recovery key is ever read.</b> The implementation enumerates protectors to
/// learn that a recovery password exists and what GUID identifies it; it never calls
/// the method that returns the password itself.
/// </para>
/// </remarks>
public interface IBitLockerCollector
{
    ValueTask<InventoryBitLocker> CollectAsync(CancellationToken cancellationToken = default);
}
