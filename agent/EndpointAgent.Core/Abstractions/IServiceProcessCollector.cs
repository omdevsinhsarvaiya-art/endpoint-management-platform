using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads the machine's Windows services and a capped running-process snapshot.
/// </summary>
/// <remarks>
/// Read-only. Service control (start/stop) and process termination are separate,
/// task-gated capabilities - listing must never imply the ability to act.
/// </remarks>
public interface IServiceProcessCollector
{
    ValueTask<IReadOnlyList<InventoryService>> CollectServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Top processes by working set, capped.</summary>
    ValueTask<IReadOnlyList<InventoryProcess>> CollectProcessesAsync(int max, CancellationToken cancellationToken = default);
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
