using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads the machine's Windows services and its running processes.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Service control (start/stop) and process termination are separate,
/// task-gated capabilities - listing must never imply the ability to act.
/// </para>
/// <para>
/// Two process methods, because two callers want different things. Inventory
/// wants a <em>summary</em> -- the biggest processes, bounded by what the server
/// will accept on a report. Force Stop wants <em>everything</em>, because it is
/// deciding which processes an application owns, and a helper that fell below a
/// working-set cut-off would be left running while the application was reported
/// stopped. That is exactly what happened when the summary method was reused
/// for the decision, so the two are now distinct by name.
/// </para>
/// </remarks>
public interface IServiceProcessCollector
{
    ValueTask<IReadOnlyList<InventoryService>> CollectServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The inventory snapshot: the top <paramref name="max"/> processes by working
    /// set, and never more than the server accepts on an inventory report (500).
    /// </summary>
    /// <remarks>
    /// A summary, not an enumeration. Not for deciding which processes an
    /// application owns -- use <see cref="CollectAllProcessesAsync"/> for that.
    /// </remarks>
    ValueTask<IReadOnlyList<InventoryProcess>> CollectProcessesAsync(int max, CancellationToken cancellationToken = default);

    /// <summary>Every process the operating system reports. No cap, no ranking.</summary>
    /// <remarks>
    /// For a decision that has to see all of them. Costs nothing the capped
    /// method does not already pay: the whole process table is walked either
    /// way; this simply keeps the result.
    /// </remarks>
    ValueTask<IReadOnlyList<InventoryProcess>> CollectAllProcessesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs the task-gated service and process control operations.
/// </summary>
/// <remarks>
/// Separate interface from the collector: read access (the collector) and write
/// access (this) are never granted together by accident. The Windows
/// implementation uses ServiceController and Process APIs - no shell (ADR-0005) -
/// and validates targets (service-name pattern, expected process image).
/// </remarks>
public interface IServiceProcessControl
{
    Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default);
    Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default);
    Task RestartServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>Terminates the PID only if its current image matches <paramref name="expectedImageName"/>.</summary>
    Task TerminateProcessAsync(int processId, string expectedImageName, CancellationToken cancellationToken = default);
}
