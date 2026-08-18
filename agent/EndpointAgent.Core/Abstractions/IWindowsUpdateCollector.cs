using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads Windows Update history and the reboot-required state.
/// </summary>
/// <remarks>
/// Read-only. Reads the LOCAL update history store (fast, no network) and the
/// reboot-required registry flags. It deliberately does NOT run an online
/// "pending updates" scan on the inventory path: that scan is slow and
/// network-dependent, and running it on every inventory would make inventory
/// unreliable. Pending-update scanning belongs to a dedicated on-demand task.
/// </remarks>
public interface IWindowsUpdateCollector
{
    ValueTask<InventoryWindowsUpdate> CollectAsync(CancellationToken cancellationToken = default);
}
