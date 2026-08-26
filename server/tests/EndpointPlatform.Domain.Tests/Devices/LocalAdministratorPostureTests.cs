using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// The standard-user-by-default verdict.
/// </summary>
/// <remarks>
/// <para>
/// The cases that matter are the exclusions. A verdict that counted every
/// administrator account would mark every Windows machine non-compliant forever,
/// because Windows creates an Administrator account and this platform refuses to
/// delete or disable it — a finding nobody could ever act on is not a finding.
/// Equally, a verdict that excluded too much would report a machine as compliant
/// while somebody is logging into it with admin rights.
/// </para>
/// <para>
/// Nothing here mutates anything. The evaluator reports; it never downgrades an
/// account.
/// </para>
/// </remarks>
public sealed class LocalAdministratorPostureTests
{
    private const string MachineSid = "S-1-5-21-1004336348-1177238915-682003330";

    private static LocalAccountView Account(
        int rid, string name, bool enabled = true, bool isAdmin = false) =>
        new($"{MachineSid}-{rid}", name, enabled, isAdmin);

    // ---- the verdict -------------------------------------------------------

    /// <summary>
    /// No inventory is not the same as a clean one.
    /// </summary>
    /// <remarks>
    /// A newly enrolled machine, or one offline since before this shipped, has
    /// reported nothing. Rendering that as Compliant would overstate how much of
    /// the estate has actually been checked.
    /// </remarks>
    [Fact]
    public void An_endpoint_that_has_reported_nothing_is_Unknown_rather_than_compliant()
    {
        LocalAdministratorPosture.Evaluate(null).Compliance
            .ShouldBe(LocalAdminCompliance.Unknown);

        LocalAdministratorPosture.Evaluate([]).Compliance
            .ShouldBe(LocalAdminCompliance.Unknown);
    }

    [Fact]
    public void A_machine_whose_only_interactive_user_is_standard_is_compliant()
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(1001, "sarah", isAdmin: false),
            Account(500, "Administrator", enabled: false, isAdmin: true),
        ]);

        result.Compliance.ShouldBe(LocalAdminCompliance.Compliant);
        result.InteractiveAdministrators.ShouldBeEmpty();
    }

    [Fact]
    public void A_machine_with_an_interactive_local_admin_is_non_compliant_and_names_it()
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(1001, "sarah", isAdmin: true),
            Account(1002, "raj", isAdmin: false),
        ]);

        result.Compliance.ShouldBe(LocalAdminCompliance.NonCompliant);
        result.InteractiveAdministrators.Select(a => a.Username).ShouldBe(["sarah"]);
    }

    [Fact]
    public void Every_offending_account_is_named_not_just_the_first()
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(1001, "sarah", isAdmin: true),
            Account(1002, "raj", isAdmin: true),
            Account(1003, "mei", isAdmin: false),
        ]);

        result.InteractiveAdministrators.Select(a => a.Username)
            .ShouldBe(["sarah", "raj"], ignoreOrder: true);
    }

    // ---- exclusions --------------------------------------------------------

    /// <summary>
    /// The built-ins are excluded, by RID and never by name.
    /// </summary>
    /// <remarks>
    /// Renaming the built-in Administrator is a standard hardening step, and the
    /// names are localized besides — so a name-based rule would stop working on a
    /// German install, or the moment somebody renamed the account, and would do
    /// so silently.
    /// </remarks>
    [Theory]
    [InlineData(500, "Administrator")]
    [InlineData(500, "RenamedRoot")]
    [InlineData(501, "Guest")]
    [InlineData(503, "DefaultAccount")]
    [InlineData(504, "WDAGUtilityAccount")]
    public void A_built_in_account_does_not_make_the_endpoint_non_compliant(int rid, string name)
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(rid, name, enabled: true, isAdmin: true),
            Account(1001, "sarah", isAdmin: false),
        ]);

        result.Compliance.ShouldBe(LocalAdminCompliance.Compliant);
    }

    [Fact]
    public void A_disabled_administrator_does_not_count_because_nobody_can_sign_in_with_it()
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(1001, "olduser", enabled: false, isAdmin: true),
            Account(1002, "sarah", isAdmin: false),
        ]);

        result.Compliance.ShouldBe(LocalAdminCompliance.Compliant);
    }

    /// <summary>
    /// Excluded is not the same as hidden.
    /// </summary>
    /// <remarks>
    /// An operator looking at a machine reported Compliant that visibly has an
    /// Administrator account needs to see why it was discounted, or they will not
    /// believe the verdict. A disabled admin in particular is one setting away
    /// from being live.
    /// </remarks>
    [Fact]
    public void Excluded_accounts_are_still_reported_with_the_reason()
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(500, "Administrator", enabled: false, isAdmin: true),
            Account(1001, "olduser", enabled: false, isAdmin: true),
            Account(1002, "sarah", isAdmin: false),
        ]);

        result.Findings.Count.ShouldBe(3);

        var builtIn = result.Findings.Single(f => f.Username == "Administrator");
        builtIn.ExcludedReason.ShouldNotBeNull();
        builtIn.CountsAgainstCompliance.ShouldBeFalse();

        var disabled = result.Findings.Single(f => f.Username == "olduser");
        disabled.ExcludedReason.ShouldNotBeNull();
        disabled.ExcludedReason!.ShouldContain("Disabled");

        result.Findings.Single(f => f.Username == "sarah").ExcludedReason.ShouldBeNull();
    }

    /// <summary>
    /// A disabled built-in is excluded once, and for the reason that is true first.
    /// </summary>
    [Fact]
    public void Disabled_is_reported_ahead_of_built_in_when_both_apply()
    {
        var result = LocalAdministratorPosture.Evaluate([
            Account(500, "Administrator", enabled: false, isAdmin: true),
        ]);

        var reason = result.Findings.Single().ExcludedReason;
        reason.ShouldNotBeNull();
        reason!.ShouldContain("Disabled");
    }

    // ---- SID parsing -------------------------------------------------------

    [Theory]
    [InlineData("S-1-5-21-1-2-3-500", true)]
    [InlineData("S-1-5-21-1-2-3-501", true)]
    [InlineData("S-1-5-21-1-2-3-503", true)]
    [InlineData("S-1-5-21-1-2-3-504", true)]
    [InlineData("S-1-5-21-1-2-3-1001", false)]
    [InlineData("S-1-5-21-1-2-3-502", false)]
    [InlineData("S-1-5-21-1-2-3-5000", false)]
    public void Built_in_detection_reads_the_RID(string sid, bool expected)
    {
        LocalAdministratorPosture.IsBuiltIn(sid).ShouldBe(expected);
    }

    /// <summary>
    /// A malformed SID is not a built-in, and does not throw.
    /// </summary>
    /// <remarks>
    /// The SID comes off an endpoint, so it is input rather than a given.
    /// Answering "not built-in" is the conservative direction: it counts the
    /// account towards the verdict rather than silently excusing it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("S-1-5-21-1-2-3-")]
    [InlineData("nonsense")]
    [InlineData("500")]
    [InlineData("S-1-5-21-1-2-3-abc")]
    public void A_malformed_SID_is_not_treated_as_built_in(string sid)
    {
        LocalAdministratorPosture.IsBuiltIn(sid).ShouldBeFalse();
    }

    /// <summary>
    /// An account with an unreadable SID still counts against the verdict.
    /// </summary>
    /// <remarks>
    /// The failure direction that matters. Excusing an administrator because its
    /// SID could not be parsed would let a malformed report produce a clean bill
    /// of health.
    /// </remarks>
    [Fact]
    public void An_administrator_with_an_unparseable_SID_still_counts()
    {
        var result = LocalAdministratorPosture.Evaluate([
            new LocalAccountView("nonsense", "mystery", Enabled: true, IsAdministrator: true),
        ]);

        result.Compliance.ShouldBe(LocalAdminCompliance.NonCompliant);
    }
}
