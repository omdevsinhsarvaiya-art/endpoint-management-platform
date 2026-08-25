using EndpointAgent.Core.Heartbeat;
using EndpointAgent.Core.Usb;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Service;

/// <summary>
/// Hosts the agent's main loops as the Windows service's background work.
/// </summary>
/// <remarks>
/// <para>
/// Thin by design: all behaviour lives in <see cref="HeartbeatLoop"/> and
/// <see cref="UsbMonitorLoop"/> (platform-neutral, unit-tested); this class only
/// adapts them to <see cref="BackgroundService"/> and makes sure a crash is
/// logged before the service host sees it.
/// </para>
/// <para>
/// The two loops run concurrently rather than interleaved. USB enforcement must
/// respond to a device being plugged in within seconds, and it must not be
/// delayed behind an inventory collection that takes considerably longer than
/// that. A fault in the USB loop is contained: it is logged and the loop
/// restarts, because losing USB enforcement is not a reason to stop
/// heartbeating, and a machine that stops heartbeating is one an administrator
/// can no longer act on at all.
/// </para>
/// </remarks>
public sealed class AgentWorker(
    HeartbeatLoop heartbeatLoop,
    UsbMonitorLoop usbMonitorLoop,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly HeartbeatLoop _heartbeatLoop = heartbeatLoop
        ?? throw new ArgumentNullException(nameof(heartbeatLoop));

    private readonly UsbMonitorLoop _usbMonitorLoop = usbMonitorLoop
        ?? throw new ArgumentNullException(nameof(usbMonitorLoop));

    private readonly ILogger<AgentWorker> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Endpoint agent worker starting (version {Version}).", _heartbeatLoop.AgentVersion);

        var usb = RunUsbLoopAsync(stoppingToken);

        try
        {
            await _heartbeatLoop.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // An unhandled exception here would stop the service silently; log it
            // as fatal and rethrow so the SCM records the failure and restarts us
            // per the service recovery policy.
            _logger.LogCritical(ex, "The agent worker crashed.");
            throw;
        }
        finally
        {
            await usb;
            _logger.LogInformation("Endpoint agent worker stopped.");
        }
    }

    /// <summary>
    /// Runs the USB loop, restarting it if it faults.
    /// </summary>
    /// <remarks>
    /// The restart matters for the security control. If this loop dies and stays
    /// dead, newly attached storage stops being restricted — silently, because
    /// the machine keeps heartbeating and looks healthy. Restarting on a short
    /// backoff means a transient fault (WMI hiccup, a driver returning an
    /// unexpected error) costs seconds of coverage rather than the rest of the
    /// service's lifetime.
    /// </remarks>
    private async Task RunUsbLoopAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _usbMonitorLoop.RunAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "The USB monitor loop faulted; restarting it in {Backoff}.", backoff);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
