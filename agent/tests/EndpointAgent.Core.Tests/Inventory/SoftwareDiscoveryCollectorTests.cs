using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// The composite collector and the pipeline it runs: how several sources' evidence
/// becomes one report, and what does and does not count as a row.
/// </summary>
public sealed class SoftwareDiscoveryCollectorTests
{
    private sealed class FixedSource(string name, params SoftwareEvidence[] evidence) : ISoftwareEvidenceSource
    {
        public string SourceName => name;

        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<SoftwareEvidence>>(evidence);
        }
    }

    private sealed class FailingSource : ISoftwareEvidenceSource
    {
        public string SourceName => "Broken";

        public ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this source cannot read its registry key");
    }

    private static SoftwareDiscoveryCollector Collector(params ISoftwareEvidenceSource[] sources) =>
        new(sources, NullLogger<SoftwareDiscoveryCollector>.Instance);

    private static SoftwareEvidence Registry(string name, string? location = @"C:\Program Files\X") =>
        new(EvidenceSource.UninstallRegistry, name, "1.0", "Contoso", null, location, "x64");

    // ---- pooling ----------------------------------------------------------------

    [Fact]
    public async Task Every_source_is_asked_and_their_evidence_is_pooled()
    {
        var first = new FixedSource("A", Registry("Alpha"));
        var second = new FixedSource("B", Registry("Beta"));

        var report = await Collector(first, second).CollectAsync();

        first.Calls.ShouldBe(1);
        second.Calls.ShouldBe(1);
        report.Select(s => s.Name).ShouldBe(["Alpha", "Beta"]);
    }

    /// <summary>
    /// One source failing costs its evidence, never the inventory. Several sources
    /// read places that are routinely half-readable; a package root that has gone
    /// missing must not blank the whole software list.
    /// </summary>
    [Fact]
    public async Task A_failing_source_is_omitted_and_the_rest_still_report()
    {
        var report = await Collector(new FailingSource(), new FixedSource("A", Registry("Alpha"))).CollectAsync();

        report.ShouldHaveSingleItem().Name.ShouldBe("Alpha");
    }

    [Fact]
    public async Task Cancellation_is_honoured_rather_than_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Collector(new FixedSource("A", Registry("Alpha"))).CollectAsync(cts.Token));
    }

    [Fact]
    public async Task No_sources_means_an_empty_report_not_an_error()
    {
        (await Collector().CollectAsync()).ShouldBeEmpty();
    }

    // ---- what is a row --------------------------------------------------------------

    /// <summary>
    /// An execution alias or a shortcut with nothing behind it is a pointer, not
    /// software. Reporting it would put a stale shortcut in the inventory as an
    /// installed application.
    /// </summary>
    [Theory]
    [InlineData(EvidenceSource.AppPaths)]
    [InlineData(EvidenceSource.StartMenuShortcut)]
    [InlineData(EvidenceSource.ExecutableMetadata)]
    public void A_reference_alone_is_not_a_row(EvidenceSource source)
    {
        var report = SoftwareDiscoveryPipeline.Run([
            new SoftwareEvidence(source, ExecutablePath: @"C:\Program Files\Contoso\app.exe"),
        ]);

        report.ShouldBeEmpty();
    }

    [Fact]
    public void An_installation_record_is_a_row()
    {
        SoftwareDiscoveryPipeline.Run([Registry("Alpha")]).ShouldHaveSingleItem().Name.ShouldBe("Alpha");
    }

    // ---- installer identity -------------------------------------------------------

    /// <summary>
    /// With the upgrade code read from the Installer registry, an MSI product's
    /// identity is the one an update keeps. Without it the product code stands in,
    /// which names one release rather than one product -- correct as far as it
    /// goes, and the reason the upgrade code is worth reading.
    /// </summary>
    [Fact]
    public void An_installer_product_is_identified_by_its_upgrade_code_when_one_is_known()
    {
        var with = ApplicationMerger.Merge([
            new SoftwareEvidence(EvidenceSource.WindowsInstaller, "Agent", "1.8.0", "Techsara",
                ProductCode: "{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}", UpgradeCode: "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}"),
        ]).ShouldHaveSingleItem();
        var without = ApplicationMerger.Merge([
            new SoftwareEvidence(EvidenceSource.WindowsInstaller, "Agent", "1.8.0", "Techsara",
                ProductCode: "{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}"),
        ]).ShouldHaveSingleItem();

        with.Identity.Kind.ShouldBe(IdentityKind.WindowsInstaller);
        with.Identity.StableKey.ShouldBe("{8f3c1d92-6b74-4a5e-9d21-7c4e8b0f5a63}");
        with.Identity.VersionKey.ShouldBe("{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}");

        without.Identity.Kind.ShouldBe(IdentityKind.WindowsInstaller);
        without.Identity.StableKey.ShouldBe("{a37aa9f3-1f80-41a2-a8f9-8b5417fad4d7}");
    }

    /// <summary>The registry view says where an entry was found. It is not the binary's architecture and is never reported as such.</summary>
    [Fact]
    public void The_registry_view_is_carried_as_where_it_was_found_not_as_architecture()
    {
        var row = SoftwareDiscoveryPipeline.Run([Registry("Alpha")]).ShouldHaveSingleItem();

        // The wire field is named Architecture for historical reasons; its value is
        // the registry view, as the dashboard labels it ("Found in / registry view").
        row.Architecture.ShouldBe("x64");
    }
}
