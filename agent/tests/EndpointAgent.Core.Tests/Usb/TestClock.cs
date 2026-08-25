namespace EndpointAgent.Core.Tests.Usb;

/// <summary>
/// A clock the test drives by hand.
/// </summary>
/// <remarks>
/// Written here rather than pulled in as a package: the USB tests need exactly
/// one capability — move time forward and see what the agent does about an
/// expired grant — and a fifteen-line class buys that without adding a
/// dependency to a test project that currently has none.
/// </remarks>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
