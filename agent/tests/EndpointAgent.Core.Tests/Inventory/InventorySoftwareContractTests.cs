using System.Text.Json;
using EndpointAgent.Core.Inventory;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// The software row on the wire: what discovery adds serializes, what an older
/// agent sends still binds, and the names discovery uses are the names the
/// contract allows.
/// </summary>
public sealed class InventorySoftwareContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static InventorySoftware Full() => new(
        "Slack", "4.52.155.0", "Slack Technologies Inc.", null,
        @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g", null,
        "User", @"OMDEVSINH-TECHS\Techsara", null,
        IdentityKind: "Package",
        StableKey: "com.tinyspeck.slackdesktop_8yrtsj140pw4g",
        VersionKey: "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g",
        Confidence: "Installed",
        Category: "Application",
        PackageFamilyName: "com.tinyspeck.slackdesktop_8yrtsj140pw4g",
        PackageFullName: "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g",
        UpgradeCode: null,
        ExecutablePath: @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g\app\Slack.exe",
        SignerSubject: "CN=\"Slack Technologies, LLC\", O=\"Slack Technologies, LLC\"",
        SignatureStatus: "Signed",
        Evidence:
        [
            new InventorySoftwareEvidence("PackageRegistration", "Slack", "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g"),
            new InventorySoftwareEvidence("StartMenuShortcut", "Slack", @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g\app\Slack.exe"),
        ]);

    [Fact]
    public void A_full_row_round_trips()
    {
        var row = Full();

        var json = JsonSerializer.Serialize(row, Web);
        var back = JsonSerializer.Deserialize<InventorySoftware>(json, Web);

        back.ShouldNotBeNull();
        (back with { Evidence = null }).ShouldBe(row with { Evidence = null }, "every scalar field survives");
        back.Evidence.ShouldNotBeNull().Count.ShouldBe(2);
        back.Evidence[0].ShouldBe(row.Evidence![0]);
        back.Evidence[1].ShouldBe(row.Evidence![1]);
    }

    [Fact]
    public void The_wire_names_are_camel_case_and_the_new_ones_are_present()
    {
        var json = JsonSerializer.Serialize(Full(), Web);

        foreach (var name in new[]
        {
            "\"identityKind\":", "\"stableKey\":", "\"versionKey\":", "\"confidence\":", "\"category\":",
            "\"packageFamilyName\":", "\"packageFullName\":", "\"executablePath\":", "\"signerSubject\":",
            "\"signatureStatus\":", "\"evidence\":", "\"source\":", "\"detail\":",
        })
        {
            json.ShouldContain(name);
        }
    }

    /// <summary>What agent 1.8.0 sends: nine fields. It binds with the rest null.</summary>
    [Fact]
    public void A_row_from_an_older_agent_binds_with_the_new_fields_absent()
    {
        const string json = """
            {"name":"Google Chrome","version":"152.0","publisher":"Google LLC","installDate":null,
             "installLocation":"C:\\Program Files\\Google\\Chrome\\Application","architecture":"x86",
             "installationScope":"Machine","installedForUser":null,"productCode":null}
            """;

        var row = JsonSerializer.Deserialize<InventorySoftware>(json, Web).ShouldNotBeNull();

        row.Name.ShouldBe("Google Chrome");
        row.IdentityKind.ShouldBeNull();
        row.Confidence.ShouldBeNull();
        row.Evidence.ShouldBeNull();
    }

    /// <summary>What an even older agent sends: six fields. Still binds.</summary>
    [Fact]
    public void A_row_with_only_the_original_six_fields_binds()
    {
        const string json = """{"name":"7-Zip","version":"23.01","publisher":"Igor Pavlov","installDate":null,"installLocation":null,"architecture":"x64"}""";

        var row = JsonSerializer.Deserialize<InventorySoftware>(json, Web).ShouldNotBeNull();

        row.InstallationScope.ShouldBeNull();
        row.Evidence.ShouldBeNull();
    }

    /// <summary>
    /// The agent names things by its enums; the contract lists what the server
    /// accepts. They must agree, or a new enum value would reject every
    /// inventory report from the agent that produced it.
    /// </summary>
    [Fact]
    public void Every_name_discovery_can_produce_is_one_the_contract_allows()
    {
        foreach (var kind in Enum.GetNames<IdentityKind>())
        {
            InventorySoftware.IdentityKinds.ShouldContain(kind);
        }

        foreach (var category in Enum.GetNames<ApplicationCategory>())
        {
            InventorySoftware.Categories.ShouldContain(category);
        }

        foreach (var status in Enum.GetNames<ExecutableSignatureStatus>())
        {
            InventorySoftware.SignatureStatuses.ShouldContain(status);
        }

        foreach (var source in Enum.GetNames<EvidenceSource>())
        {
            InventorySoftwareEvidence.Sources.ShouldContain(source);
        }

        // Only the reportable confidences are on the wire; the others never are.
        foreach (var confidence in Enum.GetValues<DiscoveryConfidence>())
        {
            var app = new DiscoveredApplication(
                new ApplicationIdentity(IdentityKind.Executable, "x"), "X", null, null, null, null, null,
                SoftwareScope.Machine, null, null, confidence, ApplicationCategory.Application, []);

            InventorySoftware.Confidences.Contains(confidence.ToString())
                .ShouldBe(SoftwareDiscoveryPipeline.IsReportable(app), confidence.ToString());
        }
    }

    [Fact]
    public void The_limits_the_agent_clamps_to_are_the_contracts()
    {
        var overlong = new DiscoveredSoftware(
            new string('n', 400), StableKey: new string('k', 2000), SignerSubject: new string('s', 600),
            ExecutablePath: @"C:\" + new string('p', 600), Category: new string('c', 40),
            Evidence: Enumerable.Range(0, 50).Select(i => new InventorySoftwareEvidence("RunningProcess", null, new string('d', 600))).ToArray());

        var row = SoftwareInventoryNormalizer.Normalize([overlong]).ShouldHaveSingleItem();

        row.Name.Length.ShouldBe(384);
        row.StableKey!.Length.ShouldBe(InventorySoftware.MaxStableKey);
        row.SignerSubject!.Length.ShouldBe(InventorySoftware.MaxSignerSubject);
        row.ExecutablePath!.Length.ShouldBe(InventorySoftware.MaxExecutablePath);
        row.Category!.Length.ShouldBe(InventorySoftware.MaxCategory);
        row.Evidence!.Count.ShouldBe(InventorySoftware.MaxEvidence);
        row.Evidence.ShouldAllBe(e => e.Detail!.Length == InventorySoftwareEvidence.MaxDetail);
    }

    [Fact]
    public void Evidence_without_a_source_is_dropped_and_an_empty_list_is_absent()
    {
        var row = SoftwareInventoryNormalizer.Normalize([
            new DiscoveredSoftware("X", Evidence: [new InventorySoftwareEvidence(" ", "n", "d"), new InventorySoftwareEvidence("AppPaths", null, null)]),
        ]).ShouldHaveSingleItem();
        row.Evidence.ShouldNotBeNull().ShouldHaveSingleItem().Source.ShouldBe("AppPaths");

        SoftwareInventoryNormalizer.Normalize([new DiscoveredSoftware("Y", Evidence: [])]).Single().Evidence.ShouldBeNull();
        SoftwareInventoryNormalizer.Normalize([new DiscoveredSoftware("Z")]).Single().Evidence.ShouldBeNull();
    }

    /// <summary>The first row for an identity wins, discovery fields included.</summary>
    [Fact]
    public void Duplicate_rows_keep_the_first_rows_discovery_fields()
    {
        var rows = SoftwareInventoryNormalizer.Normalize([
            new DiscoveredSoftware("Chrome", "1", "Google", Confidence: "Installed", Category: "Application"),
            new DiscoveredSoftware("Chrome", "1", "Google", Confidence: "Observed", Category: "Observed"),
        ]);

        rows.ShouldHaveSingleItem().Confidence.ShouldBe("Installed");
    }
}
