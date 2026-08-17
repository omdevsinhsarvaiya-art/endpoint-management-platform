using EndpointAgent.Core.Heartbeat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Service;

/// <summary>
/// Hosts the agent's main loop as the Windows service's background work.
/// </summary>
/// <remarks>
/// Thin by design: all behaviour lives in <see cref="HeartbeatLoop"/>
/// (platform-neutral, unit-tested); this class only adapts it to
/// <see cref="BackgroundService"/> and makes sure a crash is logged before the
/// service host sees it.
/// </remarks>
public sealed class AgentWorker(HeartbeatLoop heartbeatLoop, ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly HeartbeatLoop _heartbeatLoop = heartbeatLoop
        ?? throw new ArgumentNullException(nameof(heartbeatLoop));

    private readonly ILogger<AgentWorker> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Endpoint agent worker starting (version {Version}).", _heartbeatLoop.AgentVersion);

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
            _logger.LogInformation("Endpoint agent worker stopped.");
        }
    }
}
