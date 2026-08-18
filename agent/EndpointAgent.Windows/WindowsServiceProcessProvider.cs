using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads and controls Windows services and processes through managed APIs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServiceController"/> and <see cref="Process"/> only - no shell,
/// no <c>sc.exe</c>, no <c>taskkill</c> (ADR-0005). This is the one type in the
/// agent that references <c>System.Diagnostics.Process</c>; it is exempt from the
/// no-Process architecture rule precisely because process control is its reviewed
/// purpose, and the exemption is asserted narrowly by AgentSafetyTests.
/// </para>
/// <para>
/// Control operations validate their target: service names must match a strict
/// pattern, and a process is terminated only when its current image name matches
/// the caller's expectation (guarding against PID reuse between listing and kill).
/// They require elevation; unelevated they throw, and the executor reports the
/// failure rather than silently succeeding.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceProcessProvider(ILogger<WindowsServiceProcessProvider> logger)
    : IServiceProcessCollector, IServiceProcessControl
{
    private readonly ILogger<WindowsServiceProcessProvider> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    // Service short names: letters, digits, and a few punctuation chars Windows allows.
    private static bool IsValidServiceName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 256
        && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ');

    public ValueTask<IReadOnlyList<InventoryService>> CollectServicesAsync(CancellationToken cancellationToken = default)
    {
        var services = new List<InventoryService>();

        try
        {
            foreach (var controller in ServiceController.GetServices())
            {
                using (controller)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        services.Add(new InventoryService(
                            controller.ServiceName,
                            controller.DisplayName,
                            controller.Status.ToString(),
                            controller.StartType.ToString()));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        _logger.LogDebug(ex, "Skipping unreadable service {Service}.", controller.ServiceName);
                    }
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Service enumeration failed; reporting none.");
        }

        return ValueTask.FromResult<IReadOnlyList<InventoryService>>(
            services.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public ValueTask<IReadOnlyList<InventoryProcess>> CollectProcessesAsync(
        int max, CancellationToken cancellationToken = default)
    {
        var processes = new List<InventoryProcess>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // Access denied to another user's/protected process module path - fine.
                    }

                    processes.Add(new InventoryProcess(
                        process.Id,
                        process.ProcessName,
                        process.WorkingSet64,
                        path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(ex, "Skipping unreadable process {Pid}.", process.Id);
                }
            }
        }

        var top = processes
            .OrderByDescending(p => p.WorkingSetBytes)
            .Take(Math.Clamp(max, 1, 500))
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<InventoryProcess>>(top);
    }

    // --- Control (task-gated, elevation-required) ---------------------------

    public Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ValidateServiceName(serviceName);
        using var controller = new ServiceController(serviceName);

        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return Task.CompletedTask;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, ControlTimeout);
        _logger.LogWarning("Service {Service} started by an authorized task.", serviceName);
        return Task.CompletedTask;
    }

    public Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ValidateServiceName(serviceName);
        using var controller = new ServiceController(serviceName);

        if (!controller.CanStop)
        {
            throw new InvalidOperationException($"Service '{serviceName}' cannot be stopped.");
        }

        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
        _logger.LogWarning("Service {Service} stopped by an authorized task.", serviceName);
        return Task.CompletedTask;
    }

    public async Task RestartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        await StopServiceAsync(serviceName, cancellationToken);
        await StartServiceAsync(serviceName, cancellationToken);
    }

    public Task TerminateProcessAsync(
        int processId, string expectedImageName, CancellationToken cancellationToken = default)
    {
        if (processId <= 4)
        {
            // 0 = System Idle, 4 = System. Never terminate core processes.
            throw new InvalidOperationException("Refusing to terminate a system process.");
        }

        using var process = Process.GetProcessById(processId);

        // Guard against PID reuse: the caller listed a process by name+PID; if the
        // PID now belongs to a different image, refuse.
        var actualName = process.ProcessName;
        var expected = expectedImageName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(actualName, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Process {processId} is '{actualName}', not the expected '{expected}'. Refusing to terminate.");
        }

        process.Kill();
        _logger.LogWarning("Process {Pid} ({Name}) terminated by an authorized task.", processId, actualName);
        return Task.CompletedTask;
    }

    private static void ValidateServiceName(string serviceName)
    {
        if (!IsValidServiceName(serviceName))
        {
            throw new ArgumentException($"'{serviceName}' is not a valid service name.", nameof(serviceName));
        }
    }
}
