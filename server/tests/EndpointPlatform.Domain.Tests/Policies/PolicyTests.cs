using EndpointPlatform.Domain.Policies;

namespace EndpointPlatform.Domain.Tests.Policies;

public sealed class PolicyTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_policy_has_version_one_current()
    {
        var p = new Policy(Org, PolicyType.ScreenLockTimeout, "Finance lock", "10 minutes");
        p.AddVersion("""{"maxTimeoutSeconds":600}""", Now);

        p.CurrentVersionNumber.ShouldBe(1);
        p.Versions.ShouldHaveSingleItem().VersionNumber.ShouldBe(1);
        p.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Adding_a_version_increments_and_retains_history()
    {
        var p = new Policy(Org, PolicyType.ScreenLockTimeout, "Lock", "d");
        var v1 = p.AddVersion("""{"maxTimeoutSeconds":600}""", Now);
        var v2 = p.AddVersion("""{"maxTimeoutSeconds":300}""", Now.AddDays(1));

        p.CurrentVersionNumber.ShouldBe(2);
        p.Versions.Count.ShouldBe(2, "historical versions are retained");
        v1.VersionNumber.ShouldBe(1);
        v2.VersionNumber.ShouldBe(2);
        v1.DesiredStateJson.ShouldContain("600", customMessage: "the old version is never mutated");
    }

    [Fact]
    public void Policy_version_exposes_no_public_mutator()
    {
        var setters = typeof(PolicyVersion).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true }).Select(p => p.Name);
        setters.ShouldBeEmpty();

        var mutators = typeof(PolicyVersion)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name);
        mutators.ShouldBeEmpty();
    }

    [Fact]
    public void Compliance_result_updates_in_place()
    {
        var r = new PolicyComplianceResult(Org, Guid.CreateVersion7(), Guid.CreateVersion7());
        var vId = Guid.CreateVersion7();

        r.Record(vId, 1, PolicyComplianceState.NonCompliant, """["too long"]""", Now);
        r.State.ShouldBe(PolicyComplianceState.NonCompliant);

        r.Record(vId, 2, PolicyComplianceState.Compliant, null, Now.AddHours(1));
        r.State.ShouldBe(PolicyComplianceState.Compliant);
        r.PolicyVersionNumber.ShouldBe(2);
        r.DeviationsJson.ShouldBeNull();
    }
}
