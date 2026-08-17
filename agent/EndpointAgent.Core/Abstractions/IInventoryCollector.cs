using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Collects the machine's full inventory snapshot.
/// </summary>
/// <remarks>
/// The Windows implementation gathers facts via WMI/CIM and managed APIs. Every
/// individual fact is best-effort: a machine with a broken WMI class reports null
/// for that fact and real values for the rest, because a partial inventory is
/// far more useful than none, and a collector crash must never take down the
/// agent service.
/// </remarks>
public interface IInventoryCollector
{
    ValueTask<InventoryReport> CollectAsync(CancellationToken cancellationToken = default);
}
