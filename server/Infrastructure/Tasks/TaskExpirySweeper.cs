using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Tasks;

/// <summary>
/// Periodically expires tasks whose deadline passed while queued or delivered.
/// </summary>
/// <remarks>
/// <para>
/// Delivery already expires a task lazily when a device polls, but a device that
/// is offline never polls - without this sweep its tasks would sit Queued forever,
/// and could fire late if the machine reappeared hours later. A restart requested
/// an hour ago should not run when a laptop finally wakes up; the sweep guarantees
/// that regardless of whether the agent ever checks in.
/// </para>
/// <para>
/// It runs in the Admin host (the management plane), resolves a scoped
/// <see cref="DeviceTaskService"/> per tick, and processes a bounded batch so a
/// large backlog is drained across several ticks rather than in one long
/// transaction. A failing tick is logged and retried on the next interval - the
/// sweeper never crashes the host.
/// </para>
/// </remarks>
public sealed class TaskExpirySweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<TaskExpirySweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Run once at startup, then on each interval tick.
        do
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var taskService = scope.ServiceProvider.GetRequiredService<DeviceTaskService>();

                // Drain in batches until a tick finds nothing more to expire.
                int expired;
                do
                {
                    expired = await taskService.SweepExpiredAsync(BatchSize, stoppingToken);
                }
                while (expired == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Shutting down.
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the host down; try again next tick.
                _logger.LogError(ex, "Task expiry sweep failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
