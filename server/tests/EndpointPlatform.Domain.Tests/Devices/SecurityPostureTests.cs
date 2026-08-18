using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

public sealed class SecurityPostureTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static DeviceSecurityPosture Posture(Action<Fields> configure)
    {
        var f = new Fields();
        configure(f);
        var p = new DeviceSecurityPosture(Guid.CreateVersion7());
        p.Apply(f.Av, f.Rtp, f.SigAge, f.FwD, f.FwPriv, f.FwPub, f.SecureBoot,
            f.TpmPresent, f.TpmEnabled, f.TpmVer, f.BitLocker, f.Admins, Now);
        return p;
    }

    private sealed class Fields
    {
        public bool? Av = true, Rtp = true, FwD = true, FwPriv = true, FwPub = true, SecureBoot = true, TpmPresent = true, TpmEnabled = true;
        public int? SigAge = 1, Admins = 1;
        public string? TpmVer = "2.0", BitLocker = "On";
    }

    [Fact]
    public void A_fully_compliant_machine_scores_100()
    {
        Posture(_ => { }).ComplianceScore().ShouldBe(100);
    }

    [Fact]
    public void Unknown_checks_are_excluded_not_counted_as_failures()
    {
        // Everything good except BitLocker and TPM are UNKNOWN (unelevated agent).
        var score = Posture(f => { f.BitLocker = null; f.TpmEnabled = null; }).ComplianceScore();
        score.ShouldBe(100, "unknown checks must not drag the score down");
    }

    [Fact]
    public void A_known_failure_lowers_the_score()
    {
        // 9 checks; one fails (Defender off) => 8/9.
        var score = Posture(f => f.Av = false).ComplianceScore();
        score.ShouldBe((int)Math.Round(100.0 * 8 / 9));
    }

    [Fact]
    public void Stale_signatures_fail_the_signature_check()
    {
        Posture(f => f.SigAge = 30).ComplianceScore().ShouldBe((int)Math.Round(100.0 * 8 / 9));
    }

    [Fact]
    public void A_machine_with_nothing_readable_scores_null()
    {
        var score = Posture(f =>
        {
            f.Av = f.Rtp = f.FwD = f.FwPriv = f.FwPub = f.SecureBoot = f.TpmEnabled = null;
            f.SigAge = null; f.BitLocker = null;
        }).ComplianceScore();
        score.ShouldBeNull();
    }

    [Fact]
    public void Out_of_range_values_are_rejected_to_null()
    {
        var p = new DeviceSecurityPosture(Guid.CreateVersion7());
        p.Apply(true, true, 99999, true, true, true, true, true, true, "2.0", "On", -5, Now);
        p.DefenderSignatureAgeDays.ShouldBeNull();
        p.LocalAdministratorCount.ShouldBeNull();
    }
}
