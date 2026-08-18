using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads the machine's installed-software list.
/// </summary>
/// <remarks>
/// Read-only. The Windows implementation reads the uninstall registry keys - it
/// never launches an installer or a shell. Deployment (running installers) is a
/// separate, task-gated capability (Phase 11).
/// </remarks>
public interface ISoftwareCollector
{
    ValueTask<IReadOnlyList<InventorySoftware>> CollectAsync(CancellationToken cancellationToken = default);
}
