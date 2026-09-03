using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// The rules that decide what counts as one installed application, and what the
/// server will accept — proven with fixtures rather than a real machine, because
/// the registry cannot be made to hold a chosen shape on a CI agent.
/// </summary>
public sealed class SoftwareInventoryNormalizerTests
{
    private static DiscoveredSoftware App(
        string? name = "Contoso Reader",
        string? version = "1.0.0",
        string? publisher = "Contoso Ltd",
        SoftwareScope scope = SoftwareScope.Machine,
        string? user = null,
        string? registryView = "x64",
        string? productCode = null) =>
        new(name, version, publisher, null, null, registryView, scope, user, productCode);

    [Fact]
    public void An_entry_without_a_display_name_is_not_an_application()
    {
        // Updates and patches occupy uninstall keys with no DisplayName.
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(name: null), App(name: "   "), App(name: "Real App")]);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("Real App");
    }

    [Fact]
    public void The_same_entry_seen_through_two_registry_views_is_one_application()
    {
        // The duplication Windows actually produces: one product, two views.
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(registryView: "x64"), App(registryView: "x86")]);

        result.Count.ShouldBe(1);
    }

    /// <summary>
    /// Two people having the same product is two installations, not a duplicate:
    /// uninstalling one leaves the other running, so collapsing them would report
    /// a machine as clean while the application is still there for someone.
    /// </summary>
    [Fact]
    public void The_same_product_installed_for_two_users_is_two_installations()
    {
        var result = SoftwareInventoryNormalizer.Normalize([
            App(scope: SoftwareScope.User, user: @"CONTOSO\alice", registryView: null),
            App(scope: SoftwareScope.User, user: @"CONTOSO\bob", registryView: null),
        ]);

        result.Count.ShouldBe(2);
        result.Select(s => s.InstalledForUser).ShouldBe([@"CONTOSO\alice", @"CONTOSO\bob"], ignoreOrder: true);
        result.ShouldAllBe(s => s.InstallationScope == "User");
    }

    [Fact]
    public void A_machine_wide_install_and_a_per_user_install_are_both_reported()
    {
        var result = SoftwareInventoryNormalizer.Normalize([
            App(scope: SoftwareScope.Machine),
            App(scope: SoftwareScope.User, user: @"CONTOSO\alice"),
        ]);

        result.Count.ShouldBe(2);
        result.Select(s => s.InstallationScope).ShouldBe(["Machine", "User"], ignoreOrder: true);
    }

    [Fact]
    public void Different_versions_of_one_product_stay_visible()
    {
        // A genuine side-by-side install; picking one silently would hide the other.
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(version: "1.0.0"), App(version: "2.0.0")]);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Two_publishers_shipping_the_same_name_are_different_applications()
    {
        var result = SoftwareInventoryNormalizer.Normalize([
            App(name: "Setup", publisher: "Contoso Ltd"),
            App(name: "Setup", publisher: "Fabrikam Inc"),
        ]);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void A_missing_version_or_publisher_does_not_drop_the_application()
    {
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(name: "No Metadata", version: null, publisher: null)]);

        result.Count.ShouldBe(1);
        result[0].Version.ShouldBeNull();
        result[0].Publisher.ShouldBeNull();
    }

    [Fact]
    public void Blank_metadata_is_reported_as_absent_rather_than_as_whitespace()
    {
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(version: "  ", publisher: "\t")]);

        result[0].Version.ShouldBeNull();
        result[0].Publisher.ShouldBeNull();
    }

    [Fact]
    public void Surrounding_whitespace_does_not_create_a_second_entry()
    {
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(name: "Contoso Reader"), App(name: "  Contoso Reader  ")]);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void A_product_code_is_carried_through_and_is_optional()
    {
        var result = SoftwareInventoryNormalizer.Normalize([
            App(name: "MSI App", productCode: "{90160000-008C-0000-1000-0000000FF1CE}"),
            App(name: "EXE App", productCode: null),
        ]);

        result.Single(s => s.Name == "MSI App").ProductCode
            .ShouldBe("{90160000-008C-0000-1000-0000000FF1CE}");
        result.Single(s => s.Name == "EXE App").ProductCode.ShouldBeNull();
    }

    /// <summary>
    /// The registry view is reported as found, and is deliberately not claimed to
    /// be the binary's architecture — Chrome and Edge are 64-bit yet register
    /// under WOW6432Node.
    /// </summary>
    [Fact]
    public void The_registry_view_is_carried_through_unchanged()
    {
        var result = SoftwareInventoryNormalizer.Normalize([
            App(name: "A", registryView: "x64"),
            App(name: "B", registryView: "x86"),
            App(name: "C", registryView: null, scope: SoftwareScope.User, user: @"CONTOSO\alice"),
        ]);

        result.Single(s => s.Name == "A").Architecture.ShouldBe("x64");
        result.Single(s => s.Name == "B").Architecture.ShouldBe("x86");
        result.Single(s => s.Name == "C").Architecture.ShouldBeNull();
    }

    [Fact]
    public void A_machine_wide_entry_never_carries_a_user()
    {
        // Attribution would be a lie: an all-users install belongs to nobody.
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(scope: SoftwareScope.Machine, user: @"CONTOSO\alice")]);

        result[0].InstalledForUser.ShouldBeNull();
        result[0].InstallationScope.ShouldBe("Machine");
    }

    /// <summary>
    /// The Agent API rejects an entire inventory report — security posture,
    /// BitLocker and drivers included — if one software field is over length.
    /// Clamping here means an odd application costs its own detail, not the
    /// machine's whole report.
    /// </summary>
    [Fact]
    public void Over_length_fields_are_truncated_rather_than_costing_the_whole_report()
    {
        var result = SoftwareInventoryNormalizer.Normalize([
            new DiscoveredSoftware(
                new string('n', 500),
                new string('v', 200),
                new string('p', 400),
                new string('d', 60),
                new string('l', 900),
                "x64"),
        ]);

        result[0].Name.Length.ShouldBe(384);
        result[0].Version!.Length.ShouldBe(128);
        result[0].Publisher!.Length.ShouldBe(256);
        result[0].InstallDate!.Length.ShouldBe(32);
        result[0].InstallLocation!.Length.ShouldBe(512);
    }

    [Fact]
    public void The_list_is_capped_below_the_servers_limit()
    {
        var many = Enumerable.Range(0, SoftwareInventoryNormalizer.MaxEntries + 250)
            .Select(i => App(name: $"App {i:D5}"));

        var result = SoftwareInventoryNormalizer.Normalize(many);

        result.Count.ShouldBe(SoftwareInventoryNormalizer.MaxEntries);
        // Comfortably under the Agent API's 8192, which rejects rather than truncates.
        result.Count.ShouldBeLessThan(8192);
    }

    [Fact]
    public void Entries_are_ordered_by_name_for_a_stable_report()
    {
        var result = SoftwareInventoryNormalizer.Normalize(
            [App(name: "Zulu"), App(name: "alpha"), App(name: "Mike")]);

        result.Select(s => s.Name).ShouldBe(["alpha", "Mike", "Zulu"]);
    }

    [Fact]
    public void An_empty_machine_produces_an_empty_list_rather_than_throwing()
    {
        SoftwareInventoryNormalizer.Normalize([]).ShouldBeEmpty();
    }
}
