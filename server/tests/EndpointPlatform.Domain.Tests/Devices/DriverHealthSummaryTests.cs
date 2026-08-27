using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// Rolling per-device verdicts up into one endpoint verdict.
///
/// The rollup is where a wrong decision becomes a wrong dashboard number, so the
/// cases that matter are the ones where "nothing is wrong" and "we know nothing"
/// could be confused, and where the platform's own disabled devices could inflate
/// a fault count.
/// </summary>
public sealed class DriverHealthSummaryTests
{
    private static DriverView Device(string name, int? problemCode) =>
        new($"PCI\\VEN_8086&DEV_{name}", name, "System", problemCode);

    [Fact]
    public void An_endpoint_that_has_reported_nothing_is_unknown_not_healthy()
    {
        var result = DriverHealthSummary.Evaluate([]);

        result.OverallState.ShouldBe(DriverHealthState.Unknown);
        result.OverallState.ShouldNotBe(DriverHealthState.Healthy);
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public void A_null_snapshot_is_unknown()
    {
        DriverHealthSummary.Evaluate(null).OverallState.ShouldBe(DriverHealthState.Unknown);
    }

    [Fact]
    public void An_endpoint_whose_devices_all_report_healthy_is_healthy()
    {
        var result = DriverHealthSummary.Evaluate([Device("a", 0), Device("b", 0)]);

        result.OverallState.ShouldBe(DriverHealthState.Healthy);
        result.Faults.ShouldBeEmpty();
        result.TotalCount.ShouldBe(2);
    }

    /// <summary>
    /// Devices reported, but not one problem state readable. That is silence, not
    /// health, and the rollup must not upgrade it.
    /// </summary>
    [Fact]
    public void An_endpoint_whose_devices_are_all_unreadable_is_unknown()
    {
        var result = DriverHealthSummary.Evaluate([Device("a", null), Device("b", null)]);

        result.OverallState.ShouldBe(DriverHealthState.Unknown);
        result.UnknownCount.ShouldBe(2);
        result.Faults.ShouldBeEmpty();
    }

    /// <summary>
    /// Partial evidence still yields a verdict about what was readable, with the
    /// unreadable devices counted separately so the reader can see the gap.
    /// </summary>
    [Fact]
    public void Some_readable_and_some_not_is_healthy_with_the_gap_reported()
    {
        var result = DriverHealthSummary.Evaluate([Device("a", 0), Device("b", null)]);

        result.OverallState.ShouldBe(DriverHealthState.Healthy);
        result.UnknownCount.ShouldBe(1);
        result.TotalCount.ShouldBe(2);
    }

    /// <summary>
    /// A fleet with USB storage restriction applied (Milestone 11a) reports code 22
    /// on every restricted device. Those must not appear as faults.
    /// </summary>
    [Fact]
    public void Disabled_devices_are_counted_separately_and_never_as_faults()
    {
        var result = DriverHealthSummary.Evaluate(
            [Device("a", 0), Device("usb1", 22), Device("usb2", 22)]);

        result.OverallState.ShouldBe(DriverHealthState.Healthy);
        result.DisabledCount.ShouldBe(2);
        result.Faults.ShouldBeEmpty();
        result.DriverFaultCount.ShouldBe(0);
        result.DeviceFaultCount.ShouldBe(0);
    }

    [Fact]
    public void One_fault_makes_the_endpoint_a_problem()
    {
        var result = DriverHealthSummary.Evaluate([Device("a", 0), Device("b", 28)]);

        result.OverallState.ShouldBe(DriverHealthState.Problem);
        result.Faults.Count.ShouldBe(1);
        result.DriverFaultCount.ShouldBe(1);
    }

    [Fact]
    public void Faults_are_counted_by_what_they_are_attributable_to()
    {
        var result = DriverHealthSummary.Evaluate(
            [Device("a", 28), Device("b", 24), Device("c", 10), Device("d", 0)]);

        result.DriverFaultCount.ShouldBe(1);
        result.DeviceFaultCount.ShouldBe(1);
        result.IndeterminateFaultCount.ShouldBe(1);
        result.Faults.Count.ShouldBe(3);
        result.TotalCount.ShouldBe(4);
    }

    /// <summary>
    /// Driver faults first because they are the only ones this platform can act on;
    /// an operator scanning the list should meet the actionable items before the
    /// "your hardware is unplugged" ones.
    /// </summary>
    [Fact]
    public void Actionable_driver_faults_are_listed_before_hardware_faults()
    {
        var result = DriverHealthSummary.Evaluate(
            [Device("hardware", 24), Device("ambiguous", 10), Device("driver", 28)]);

        result.Faults[0].Verdict.FaultKind.ShouldBe(DriverFaultKind.Driver);
        result.Faults[1].Verdict.FaultKind.ShouldBe(DriverFaultKind.Device);
        result.Faults[2].Verdict.FaultKind.ShouldBe(DriverFaultKind.Indeterminate);
    }

    [Fact]
    public void Every_fault_carries_the_code_and_the_description_an_operator_needs()
    {
        var fault = DriverHealthSummary.Evaluate([Device("b", 28)]).Faults.Single();

        fault.Verdict.ProblemCode.ShouldBe(28);
        fault.Verdict.Description.ShouldNotBeNullOrWhiteSpace();
        fault.InstanceId.ShouldNotBeNullOrWhiteSpace();
    }
}
