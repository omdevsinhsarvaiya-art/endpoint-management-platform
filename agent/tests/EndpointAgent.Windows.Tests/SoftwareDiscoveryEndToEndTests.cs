using EndpointAgent.Core.Inventory;
using EndpointAgent.Windows;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The software collector exactly as the service composes it, against this
/// machine, held to the invariants that make adding a discovery source safe.
/// </summary>
/// <remarks>
/// <para>
/// The registry alone reported a list before this milestone. Only installation
/// records add rows -- the uninstall registry's, and now package registrations'
/// -- and every registry row is still reported as it was. The supplementary
/// sources may add to what is known about a row but may not add one, remove
/// one, or change its identity. The one field they may fill is an install
/// location the record left empty, because that is the field they exist to
/// recover -- and only ever from empty, never over a recorded value.
/// </para>
/// <para>
/// Which products this machine has is not asserted; that the report obeys these
/// rules on any machine is.
/// </para>
/// </remarks>
public sealed class SoftwareDiscoveryEndToEndTests
{
    /// <summary>The composition in <c>Program.cs</c>, built by hand so the test cannot drift from it silently.</summary>
    private static (WindowsSoftwareCollector Registry, WindowsPackageRegistrationEvidenceSource Packages, SoftwareDiscoveryCollector Composite) Production()
    {
        var registry = new WindowsSoftwareCollector(
            NullLogger<WindowsSoftwareCollector>.Instance,
            new WindowsInstallLocationResolver(NullLogger<WindowsInstallLocationResolver>.Instance),
            new WindowsUpgradeCodeIndex(NullLogger<WindowsUpgradeCodeIndex>.Instance));
        var packages = new WindowsPackageRegistrationEvidenceSource(NullLogger<WindowsPackageRegistrationEvidenceSource>.Instance);

        var composite = new SoftwareDiscoveryCollector(
            [
                registry,
                packages,
                new WindowsAppPathsEvidenceSource(NullLogger<WindowsAppPathsEvidenceSource>.Instance),
                new WindowsStartMenuEvidenceSource(NullLogger<WindowsStartMenuEvidenceSource>.Instance),
            ],
            NullLogger<SoftwareDiscoveryCollector>.Instance,
            new WindowsExecutableMetadataReader());

        return (registry, packages, composite);
    }

    private static string RowIdentity(InventorySoftware s) =>
        string.Join((char)0x1F, s.Name, s.Version, s.Publisher, s.InstallationScope, s.InstalledForUser);

    [Fact]
    public async Task Only_installation_records_add_rows_and_none_are_removed()
    {
        var (registry, packages, composite) = Production();

        var fromRegistry = await registry.CollectAsync(CancellationToken.None);
        var fromPackages = SoftwareDiscoveryPipeline.Run(await packages.CollectEvidenceAsync(CancellationToken.None));
        var after = await composite.CollectAsync(CancellationToken.None);

        var expected = fromRegistry.Concat(fromPackages).Select(RowIdentity).Distinct(StringComparer.OrdinalIgnoreCase);

        after.Select(RowIdentity).OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_registry_row_location_is_only_ever_filled_never_changed()
    {
        var (registry, _, composite) = Production();

        var before = (await registry.CollectAsync(CancellationToken.None)).ToDictionary(RowIdentity, s => s, StringComparer.Ordinal);
        var after = await composite.CollectAsync(CancellationToken.None);

        foreach (var row in after)
        {
            if (!before.TryGetValue(RowIdentity(row), out var original))
            {
                continue; // A package row; the registry never reported it.
            }

            if (original.InstallLocation is not null)
            {
                row.InstallLocation.ShouldBe(original.InstallLocation, $"{row.Name}: a recorded location is reported as recorded");
            }

            row.ProductCode.ShouldBe(original.ProductCode);
            row.Architecture.ShouldBe(original.Architecture);
            row.InstallDate.ShouldBe(original.InstallDate);
        }
    }

    [Fact]
    public async Task Every_reported_install_location_is_one_the_matcher_accepts()
    {
        var (_, _, composite) = Production();

        var software = await composite.CollectAsync(CancellationToken.None);

        software.Where(s => s.InstallLocation is not null)
            .Where(s => !ApplicationProcessMatcher.CanResolve(s.InstallLocation))
            .Select(s => $"{s.Name} -> {s.InstallLocation}")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Never_reports_the_agents_own_directory_as_an_install_location()
    {
        var (_, _, composite) = Production();
        var self = AppContext.BaseDirectory.TrimEnd('\\');

        var software = await composite.CollectAsync(CancellationToken.None);

        software.Where(s => s.InstallLocation is not null)
            .ShouldAllBe(s => !self.StartsWith(s.InstallLocation!.Trim('"').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_row_is_a_reference_and_every_installed_application_has_an_installation_record()
    {
        var (_, _, composite) = Production();

        var evidence = await composite.CollectEvidenceAsync(CancellationToken.None);
        var applications = ApplicationMerger.Merge(evidence);

        applications.Where(a => a.Confidence == DiscoveryConfidence.Installed)
            .ShouldAllBe(a => a.Evidence.Any(e => e.IsAuthoritative));
        applications.Where(a => a.Confidence == DiscoveryConfidence.Referenced)
            .ShouldAllBe(a => a.Evidence.All(e => !e.IsAuthoritative));
        applications.ShouldAllBe(a => a.Evidence.Count > 0);

        SoftwareDiscoveryPipeline.Run(evidence).Count
            .ShouldBe(applications.Count(SoftwareDiscoveryPipeline.IsReportable) - Duplicates(applications));
    }

    /// <summary>Applications the normalizer would still fold: same row identity under distinct stable keys.</summary>
    private static int Duplicates(IReadOnlyList<DiscoveredApplication> applications)
    {
        var rows = applications.Where(SoftwareDiscoveryPipeline.IsReportable)
            .Select(a => string.Join((char)0x1F, a.Name, a.Version, a.Publisher, a.Scope, a.Scope == SoftwareScope.User ? a.InstalledForUser : null))
            .ToList();

        return rows.Count - rows.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    [Fact]
    public async Task The_report_still_fits_the_wire()
    {
        var (_, _, composite) = Production();

        var software = await composite.CollectAsync(CancellationToken.None);

        software.Count.ShouldBeLessThan(8192);
        software.ShouldAllBe(s => s.Name.Length <= 384);
        software.ShouldAllBe(s => s.InstallLocation == null || s.InstallLocation.Length <= 512);
    }
}
