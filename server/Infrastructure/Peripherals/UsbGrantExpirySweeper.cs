using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Peripherals;

/// <summary>
/// Moves lapsed USB access grants to Expired and returns their devices to
/// Restricted in the console's view of the world.
/// </summary>
/// <remarks>
/// <para>
/// This sweep does not stop access — it records that access has already
/// stopped. Two independent mechanisms have ended it before this runs: the agent
/// restricts the device against its own clock when the deadline passes (so a
/// machine that never reaches the server again still loses access on time), and
/// <c>UsbService.BuildPolicyAsync</c> computes liveness from the clock, so a
/// lapsed grant is absent from every policy the server publishes regardless of
/// its stored status. If this sweeper never ran, the security outcome would be
/// unchanged and only the console would show stale rows.
/// </para>
/// <para>
/// Kept separate from <c>TaskExpirySweeper</c> despite the identical cadence so
/// that a failure in one does not silently stop the other.
/// </para>
/// </remarks>
public sealed class UsbGrantExpirySweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<UsbGrantExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<UsbGrantExpirySweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var usbService = scope.ServiceProvider.GetRequiredService<UsbService>();

                int expired;
                do
                {
                    expired = await usbService.SweepExpiredGrantsAsync(BatchSize, stoppingToken);
                }
                while (expired == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "USB grant expiry sweep failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
