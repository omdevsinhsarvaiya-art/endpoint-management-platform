using EndpointAgent.Core.Heartbeat;

namespace EndpointAgent.Core.Tests.Heartbeat;

public sealed class HeartbeatLoopTests
{
    [Fact]
    public void Backoff_never_drops_below_thirty_seconds()
    {
        for (var failures = 1; failures <= 20; failures++)
        {
            for (var sample = 0; sample < 25; sample++)
            {
                HeartbeatLoop.ComputeBackoff(failures)
                    .ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public void Backoff_never_exceeds_five_minutes()
    {
        for (var failures = 1; failures <= 20; failures++)
        {
            for (var sample = 0; sample < 25; sample++)
            {
                HeartbeatLoop.ComputeBackoff(failures)
                    .ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
            }
        }
    }

    [Fact]
    public void Backoff_is_jittered_rather_than_fixed()
    {
        // A fleet of agents that all retry at exactly the same instant is a
        // self-inflicted DDoS against a recovering server. Sampling the backoff
        // for a fixed failure count must produce spread, not one value.
        var samples = Enumerable.Range(0, 50)
            .Select(_ => HeartbeatLoop.ComputeBackoff(5))
            .Distinct()
            .Count();

        samples.ShouldBeGreaterThan(10, "backoff must be jittered");
    }
}
