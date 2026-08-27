using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Identity;

/// <summary>
/// Marks lapsed administrator elevations Expired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bookkeeping, not enforcement.</b> Access has already ended by the time this
/// runs: the endpoint withdraws the rights against its own clock, and
/// <see cref="LocalAdminElevationService.BuildDesiredElevationsAsync"/> stops
/// publishing a lapsed elevation the instant its deadline passes. If this
/// sweeper never ran again, no account would keep administrator rights past its
/// deadline -- only the console would show a stale label.
/// </para>
/// <para>
/// That property is deliberate and load-bearing. A design where expiry depended
/// on a server process running on time would mean a paused container, a failed
/// deploy or a long GC pause silently extended someone's administrator rights.
/// </para>
/// <para>
/// Kept separate from the USB and task sweepers despite the identical cadence, so
/// a failure in one cannot silently stop the others.
/// </para>
/// </remarks>
public sealed class LocalAdminElevationExpirySweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<LocalAdminElevationExpirySweeper> logger) : BackgroundService
{
    /// <summary>
    /// Matches the USB grant sweeper. The interval bounds only how stale a
    /// console label can be, not how long rights persist.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<LocalAdminElevationExpirySweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<LocalAdminElevationService>();

                int expired;
                do
                {
                    expired = await service.SweepExpiredAsync(BatchSize, stoppingToken);
                }
                while (expired == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Elevation expiry sweep failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
