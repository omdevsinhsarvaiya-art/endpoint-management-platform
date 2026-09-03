using EndpointPlatform.Domain.Software;

namespace EndpointPlatform.Domain.Tests.Software;

/// <summary>
/// The decision that stops a deployment reinstalling software that is already
/// correct — proven across the matrix that matters: missing, same, older, newer,
/// and unreadable.
/// </summary>
public sealed class SoftwareEligibilityTests
{
    private static readonly DeployableSoftware Chrome =
        new("Google Chrome", "152.0.7977.75", "Google LLC", "{8A69D345-D564-463C-AFF1-A69D9E530F96}");

    private static InstalledApplication Installed(
        string name = "Google Chrome",
        string? version = "152.0.7977.75",
        string? publisher = "Google LLC",
        string? productCode = null) =>
        new(name, version, publisher, productCode);

    [Fact]
    public void Software_that_is_not_present_needs_installing()
    {
        SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(name: "Something Else")])
            .ShouldBe(SoftwareEligibility.InstallRequired);

        SoftwareEligibilityEvaluator.Evaluate(Chrome, [])
            .ShouldBe(SoftwareEligibility.InstallRequired);
    }

    [Fact]
    public void The_requested_version_already_being_present_creates_no_task()
    {
        var result = SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed()]);

        result.ShouldBe(SoftwareEligibility.AlreadyInstalled);
        result.NeedsInstall().ShouldBeFalse();
    }

    [Fact]
    public void An_older_version_needs_updating()
    {
        var result = SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(version: "151.0.7900.10")]);

        result.ShouldBe(SoftwareEligibility.UpdateRequired);
        result.NeedsInstall().ShouldBeTrue();
    }

    /// <summary>
    /// Installing an older package over a newer install is a downgrade, and this
    /// platform does not do that silently.
    /// </summary>
    [Fact]
    public void A_newer_version_already_installed_is_never_downgraded()
    {
        var result = SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(version: "153.0.1.0")]);

        result.ShouldBe(SoftwareEligibility.NewerInstalled);
        result.NeedsInstall().ShouldBeFalse();
    }

    /// <summary>
    /// Something is installed but its version cannot be ordered. Installing over
    /// it might be a downgrade, so the operator is told rather than the platform
    /// guessing.
    /// </summary>
    [Fact]
    public void An_uncomparable_installed_version_is_reported_not_guessed()
    {
        var result = SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(version: "unknown-build")]);

        result.ShouldBe(SoftwareEligibility.VersionNotComparable);
        result.NeedsInstall().ShouldBeFalse();
    }

    /// <summary>
    /// Two identical unreadable versions still mean the device is correct — the
    /// alternative is reinstalling it on every single deployment forever.
    /// </summary>
    [Fact]
    public void An_identical_unreadable_version_counts_as_installed()
    {
        var odd = new DeployableSoftware("Contoso Tool", "SPRING-RELEASE", "Contoso", "{11111111-1111-1111-1111-111111111111}");

        SoftwareEligibilityEvaluator.Evaluate(odd, [Installed("Contoso Tool", "SPRING-RELEASE", "Contoso")])
            .ShouldBe(SoftwareEligibility.AlreadyInstalled);
    }

    [Fact]
    public void A_missing_installed_version_cannot_be_ordered()
    {
        SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(version: null)])
            .ShouldBe(SoftwareEligibility.VersionNotComparable);
    }

    /// <summary>
    /// The product code is the reliable MSI identity: it survives a renamed
    /// display name, which a name match would miss entirely.
    /// </summary>
    [Fact]
    public void A_product_code_match_identifies_the_product_despite_a_different_name()
    {
        var renamed = Installed(
            name: "Chrome (Enterprise)", publisher: "Someone Else",
            productCode: "{8A69D345-D564-463C-AFF1-A69D9E530F96}");

        SoftwareEligibilityEvaluator.Evaluate(Chrome, [renamed])
            .ShouldBe(SoftwareEligibility.AlreadyInstalled);
    }

    /// <summary>
    /// A different product code is a different product, even under the same name —
    /// otherwise two vendors' "Setup" would be treated as one application.
    /// </summary>
    [Fact]
    public void A_different_product_code_is_a_different_product()
    {
        var other = Installed(productCode: "{99999999-9999-9999-9999-999999999999}");

        SoftwareEligibilityEvaluator.Evaluate(Chrome, [other])
            .ShouldBe(SoftwareEligibility.InstallRequired);
    }

    /// <summary>
    /// Barely half of real installed entries carry a product code, so non-MSI
    /// software has to match on name and publisher or nothing would ever match.
    /// </summary>
    [Fact]
    public void Non_msi_software_matches_on_name_and_publisher()
    {
        SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(productCode: null)])
            .ShouldBe(SoftwareEligibility.AlreadyInstalled);

        SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(publisher: "Impostor Inc", productCode: null)])
            .ShouldBe(SoftwareEligibility.InstallRequired);
    }

    /// <summary>
    /// Inventory often omits the publisher. Treating that as "a different product"
    /// would reinstall software that is already present.
    /// </summary>
    [Fact]
    public void A_missing_publisher_on_either_side_does_not_prevent_a_name_match()
    {
        SoftwareEligibilityEvaluator.Evaluate(Chrome, [Installed(publisher: null, productCode: null)])
            .ShouldBe(SoftwareEligibility.AlreadyInstalled);
    }

    /// <summary>
    /// Since 1.5.0 the same product can appear once per user plus machine-wide.
    /// If anyone already has the requested version there is nothing to send.
    /// </summary>
    [Fact]
    public void When_one_of_several_installs_is_current_nothing_is_deployed()
    {
        var result = SoftwareEligibilityEvaluator.Evaluate(Chrome, [
            Installed(version: "151.0.7900.10"),
            Installed(version: "152.0.7977.75"),
        ]);

        result.ShouldBe(SoftwareEligibility.AlreadyInstalled);
    }

    [Fact]
    public void When_every_install_is_older_an_update_is_required()
    {
        var result = SoftwareEligibilityEvaluator.Evaluate(Chrome, [
            Installed(version: "150.0.1.0"),
            Installed(version: "151.0.7900.10"),
        ]);

        result.ShouldBe(SoftwareEligibility.UpdateRequired);
    }
}

/// <summary>
/// Ordering the version strings Windows applications actually report, including
/// the shapes the platform's own agent-version parser deliberately refuses.
/// </summary>
public sealed class SoftwareVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.0.10")]      // The bug string comparison introduces.
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("152.0.7977.65", "152.0.7977.75")]
    [InlineData("1.4", "1.5")]
    public void Older_versions_order_before_newer_ones(string older, string newer)
    {
        SoftwareVersion.Compare(older, newer).ShouldNotBeNull().ShouldBeLessThan(0);
        SoftwareVersion.Compare(newer, older).ShouldNotBeNull().ShouldBeGreaterThan(0);
    }

    /// <summary>A missing component is zero, so 1.5 and 1.5.0 are one version.</summary>
    [Theory]
    [InlineData("1.5", "1.5.0")]
    [InlineData("1.5.0", "1.5.0.0")]
    [InlineData("2", "2.0.0.0")]
    public void Absent_components_count_as_zero(string left, string right)
    {
        SoftwareVersion.Compare(left, right).ShouldBe(0);
        SoftwareVersion.AreSame(left, right).ShouldBeTrue();
    }

    /// <summary>
    /// Zoom reports "7.1.5 (43453)". The parenthesised build is the vendor's
    /// annotation, not a version component.
    /// </summary>
    [Fact]
    public void A_trailing_build_tag_is_ignored()
    {
        SoftwareVersion.AreSame("7.1.5 (43453)", "7.1.5").ShouldBeTrue();
        SoftwareVersion.Compare("7.1.5 (43453)", "7.1.6").ShouldNotBeNull().ShouldBeLessThan(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void Unreadable_versions_are_not_comparable_rather_than_guessed(string? value)
    {
        SoftwareVersion.Compare(value, "1.0.0").ShouldBeNull();
        SoftwareVersion.Compare("1.0.0", value).ShouldBeNull();
    }

    /// <summary>Identical text is the same version even when it cannot be parsed.</summary>
    [Fact]
    public void Identical_unparseable_text_is_still_the_same_version()
    {
        SoftwareVersion.AreSame("SPRING-RELEASE", "spring-release").ShouldBeTrue();
        SoftwareVersion.AreSame("SPRING-RELEASE", "AUTUMN-RELEASE").ShouldBeFalse();
    }

    /// <summary>Parsing stops at a non-numeric component rather than skipping it.</summary>
    [Fact]
    public void Parsing_stops_at_the_first_non_numeric_component()
    {
        // "1.x.5" is 1, never 1.5 -- skipping would invent an ordering.
        SoftwareVersion.Compare("1.x.5", "1.5.0").ShouldNotBeNull().ShouldBeLessThan(0);
    }

    [Fact]
    public void An_absurdly_long_component_is_refused_rather_than_overflowing()
    {
        SoftwareVersion.Compare("99999999999999999999.0", "1.0").ShouldBeNull();
    }
}
