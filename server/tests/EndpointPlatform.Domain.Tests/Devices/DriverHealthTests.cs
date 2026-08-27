using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// How a Windows problem code becomes a health verdict.
///
/// Three things here carry real weight. **Unknown is not healthy** — a device whose
/// problem state could not be read must never be counted as fine. **Disabled is not
/// a fault** — this platform disables devices itself, and its own USB restriction
/// must not read as fleet-wide damage. And **attribution is refused when it is not
/// clear**, because sending an operator to replace hardware over a bad driver (or
/// the reverse) is worse than telling them we do not know.
/// </summary>
public sealed class DriverHealthTests
{
    [Fact]
    public void No_problem_code_is_healthy()
    {
        var verdict = DriverHealth.Classify(0);

        verdict.State.ShouldBe(DriverHealthState.Healthy);
        verdict.FaultKind.ShouldBe(DriverFaultKind.None);
        verdict.CountsAsFault.ShouldBeFalse();
    }

    /// <summary>
    /// The distinction the whole nullable problem code exists for. Null means the
    /// endpoint could not read the devnode; treating that as the zero that means
    /// "working properly" would invent evidence.
    /// </summary>
    [Fact]
    public void An_unread_problem_state_is_unknown_and_never_healthy()
    {
        var verdict = DriverHealth.Classify(null);

        verdict.State.ShouldBe(DriverHealthState.Unknown);
        verdict.State.ShouldNotBe(DriverHealthState.Healthy);
        verdict.CountsAsFault.ShouldBeFalse();
        verdict.ProblemCode.ShouldBeNull();
    }

    /// <summary>
    /// Code 22 is what Milestone 11a's USB storage restriction produces on every
    /// restricted device. If it counted as a fault, correctly restricting a fleet
    /// would light up every endpoint as broken.
    /// </summary>
    [Fact]
    public void An_administratively_disabled_device_is_not_a_fault()
    {
        var verdict = DriverHealth.Classify(22);

        verdict.State.ShouldBe(DriverHealthState.Disabled);
        verdict.State.ShouldNotBe(DriverHealthState.Problem);
        verdict.FaultKind.ShouldBe(DriverFaultKind.None);
        verdict.CountsAsFault.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]   // no driver installed
    [InlineData(18)]  // must be reinstalled
    [InlineData(19)]  // corrupt driver registry data
    [InlineData(28)]  // drivers not installed
    [InlineData(31)]  // Windows cannot load the drivers
    [InlineData(32)]  // driver service disabled
    [InlineData(38)]  // previous instance still in memory
    [InlineData(39)]  // driver service key invalid
    [InlineData(48)]  // driver blocked
    [InlineData(52)]  // signature could not be verified
    public void Driver_faults_are_attributed_to_the_driver(int code)
    {
        var verdict = DriverHealth.Classify(code);

        verdict.State.ShouldBe(DriverHealthState.Problem);
        verdict.FaultKind.ShouldBe(DriverFaultKind.Driver);
        verdict.CountsAsFault.ShouldBeTrue();
    }

    [Theory]
    [InlineData(12)]  // resource conflict
    [InlineData(24)]  // device not present
    [InlineData(43)]  // hardware reported a problem
    [InlineData(45)]  // not currently connected
    public void Hardware_faults_are_attributed_to_the_device(int code)
    {
        var verdict = DriverHealth.Classify(code);

        verdict.State.ShouldBe(DriverHealthState.Problem);
        verdict.FaultKind.ShouldBe(DriverFaultKind.Device);
        verdict.CountsAsFault.ShouldBeTrue();
    }

    /// <summary>
    /// Code 10 is the commonest problem code and the least diagnostic: Windows does
    /// not say whether the driver or the hardware failed. Guessing would send half
    /// of these to the wrong remedy.
    /// </summary>
    [Fact]
    public void An_ambiguous_problem_is_reported_as_a_problem_without_attribution()
    {
        var verdict = DriverHealth.Classify(10);

        verdict.State.ShouldBe(DriverHealthState.Problem);
        verdict.FaultKind.ShouldBe(DriverFaultKind.Indeterminate);
        verdict.CountsAsFault.ShouldBeTrue();
    }

    [Fact]
    public void An_unrecognised_code_is_still_a_problem()
    {
        var verdict = DriverHealth.Classify(999);

        verdict.State.ShouldBe(DriverHealthState.Problem);
        verdict.FaultKind.ShouldBe(DriverFaultKind.Indeterminate);
        verdict.ProblemCode.ShouldBe(999);
        verdict.Description.ShouldContain("999");
    }

    /// <summary>
    /// The description is fixed text chosen by this code from the problem code, so
    /// nothing an endpoint sends can become the text a console renders.
    /// </summary>
    [Fact]
    public void Every_verdict_carries_a_description()
    {
        int?[] codes = [null, 0, 1, 10, 22, 24, 43, 4242];

        foreach (var code in codes)
        {
            DriverHealth.Classify(code).Description.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
