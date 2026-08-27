using EndpointAgent.Core.Abstractions;

namespace EndpointAgent.Core.Tests.Drivers;

/// <summary>
/// Turning per-instance verification results into one overall outcome.
///
/// The case this exists for is a hardware id that matches several devices where the
/// driver takes on some and not others. Averaging that into a success — or reading
/// the count of successes and calling it done — would report a machine as fixed
/// while one of its devices is still broken.
/// </summary>
public sealed class DriverInstallOutcomeTests
{
    private static DriverInstanceVerification Instance(string id, bool verified) =>
        new(id, verified, "2.0.0.0", "Contoso", "oem42.inf", verified ? 0 : 10,
            verified ? null : "the device reports Windows problem code 10");

    [Fact]
    public void Every_instance_verified_is_verified_overall()
    {
        var outcome = DriverInstallOutcome.FromVerifications(
            [Instance("INST\\1", true), Instance("INST\\2", true)], rebootRequired: false);

        outcome.Result.ShouldBe(DriverInstallResult.Verified);
        outcome.Succeeded.ShouldBeTrue();
        outcome.Instances.Count.ShouldBe(2);
    }

    /// <summary>
    /// The assertion this whole file is for. One device taking the driver does not
    /// make the installation a success while another did not.
    /// </summary>
    [Fact]
    public void One_failed_instance_makes_the_whole_outcome_a_failure()
    {
        var outcome = DriverInstallOutcome.FromVerifications(
            [Instance("INST\\good", true), Instance("INST\\bad", false)], rebootRequired: false);

        outcome.Result.ShouldBe(DriverInstallResult.VerificationFailed);
        outcome.Result.ShouldNotBe(DriverInstallResult.Verified);
        outcome.Succeeded.ShouldBeFalse();
    }

    /// <summary>Even when the failures are the minority by a wide margin.</summary>
    [Fact]
    public void A_single_failure_among_many_successes_still_fails()
    {
        var instances = Enumerable.Range(0, 9)
            .Select(i => Instance($"INST\\{i}", verified: true))
            .Append(Instance("INST\\9", verified: false))
            .ToList();

        DriverInstallOutcome.FromVerifications(instances, rebootRequired: false)
            .Result.ShouldBe(DriverInstallResult.VerificationFailed);
    }

    /// <summary>
    /// Both instances survive in the outcome, whatever the verdict. An operator
    /// needs to know which device failed, not just that one did.
    /// </summary>
    [Fact]
    public void Both_the_passing_and_failing_instances_are_reported()
    {
        var outcome = DriverInstallOutcome.FromVerifications(
            [Instance("INST\\good", true), Instance("INST\\bad", false)], rebootRequired: false);

        outcome.Instances.Count.ShouldBe(2);
        outcome.Instances.Count(i => i.Verified).ShouldBe(1);
        outcome.Instances.Count(i => !i.Verified).ShouldBe(1);

        outcome.Detail.ShouldNotBeNull();
        outcome.Detail!.ShouldContain("INST\\bad");
        outcome.Detail.ShouldNotContain("INST\\good");
    }

    /// <summary>
    /// Until the machine restarts, the devices legitimately still show the old
    /// driver. Reporting that as a verification failure would turn a correct
    /// installation into an error somebody retries.
    /// </summary>
    [Fact]
    public void A_pending_reboot_outranks_per_instance_verification()
    {
        var outcome = DriverInstallOutcome.FromVerifications(
            [Instance("INST\\1", false), Instance("INST\\2", false)], rebootRequired: true);

        outcome.Result.ShouldBe(DriverInstallResult.PendingReboot);
        outcome.Succeeded.ShouldBeTrue();
        outcome.Detail.ShouldNotBeNull();
        outcome.Detail!.ShouldContain("restart");
    }

    [Fact]
    public void A_pending_reboot_is_never_reported_as_verified()
    {
        DriverInstallOutcome.FromVerifications([Instance("INST\\1", true)], rebootRequired: true)
            .Result.ShouldNotBe(DriverInstallResult.Verified);
    }

    /// <summary>
    /// An installation that affected nothing verified nothing. Absence of evidence
    /// is not evidence of success.
    /// </summary>
    [Fact]
    public void No_instances_to_verify_is_a_failure_not_a_quiet_success()
    {
        var outcome = DriverInstallOutcome.FromVerifications([], rebootRequired: false);

        outcome.Result.ShouldBe(DriverInstallResult.VerificationFailed);
        outcome.Succeeded.ShouldBeFalse();
    }
}
