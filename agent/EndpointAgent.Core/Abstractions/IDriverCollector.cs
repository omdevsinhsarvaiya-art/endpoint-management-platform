using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads the machine's PnP devices and their bound drivers.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and deliberately so: this interface has no counterpart that installs
/// or changes a driver. Driver remediation is a separate, task-gated capability with
/// its own abstraction, so nothing that merely collects inventory can be extended
/// into a mutation by accident.
/// </para>
/// <para>
/// The collector reports raw Windows facts -- notably the <c>CM_PROB_*</c> problem
/// code -- and does not judge them. Classification lives on the server, so changing
/// how a problem code is interpreted does not require a fleet-wide agent rollout.
/// </para>
/// </remarks>
public interface IDriverCollector
{
    ValueTask<IReadOnlyList<InventoryDriver>> CollectAsync(CancellationToken cancellationToken = default);
}
