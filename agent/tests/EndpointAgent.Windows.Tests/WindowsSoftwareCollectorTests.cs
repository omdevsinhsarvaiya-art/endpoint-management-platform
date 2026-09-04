using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration test against this machine's real uninstall registry.
/// Read-only; asserts invariants any real machine satisfies.
/// </summary>
/// <remarks>
/// Deliberately asserts no particular application by name. Which products a CI
/// agent or a developer laptop happens to have installed is not a property of
/// this code, and a test that depended on it would fail for the wrong reason.
/// The shape of what is collected is asserted here; the rules that decide what
/// counts as one application are proven with fixtures in
/// <c>SoftwareInventoryNormalizerTests</c>.
/// </remarks>
public sealed class WindowsSoftwareCollectorTests
{
    private static WindowsSoftwareCollector Create() =>
        new(
            NullLogger<WindowsSoftwareCollector>.Instance,
            new WindowsInstallLocationResolver(NullLogger<WindowsInstallLocationResolver>.Instance));

    [Fact]
    public async Task Collects_installed_software_without_throwing()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        // A developer machine with .NET, VS Code etc. always has entries.
        software.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Every_entry_has_a_non_empty_name()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        software.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Name));
    }

    [Fact]
    public async Task Entries_are_deduplicated_by_installation_identity()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        var keys = software
            .Select(s => $"{s.Name}|{s.Version}|{s.Publisher}|{s.InstallationScope}|{s.InstalledForUser}")
            .ToList();

        keys.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(keys.Count);
    }

    /// <summary>
    /// Every entry declares whether it is an all-users or a per-user install.
    /// Before 1.5.0 there was no such distinction and per-user software was not
    /// collected at all, because the agent read HKCU while running as LocalSystem.
    /// </summary>
    [Fact]
    public async Task Every_entry_declares_its_installation_scope()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        software.ShouldAllBe(s => s.InstallationScope == "Machine" || s.InstallationScope == "User");
    }

    /// <summary>
    /// Attribution is exactly as good as the scope claims: a per-user install
    /// names the account it belongs to, and a machine-wide one names nobody,
    /// because an all-users install genuinely belongs to no single account.
    /// </summary>
    [Fact]
    public async Task User_attribution_matches_the_declared_scope()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        software.Where(s => s.InstallationScope == "Machine")
            .ShouldAllBe(s => s.InstalledForUser == null);

        software.Where(s => s.InstallationScope == "User")
            .ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.InstalledForUser));
    }

    /// <summary>
    /// A product code, where present, is a real GUID in registry form - it is the
    /// join between an installed application and an approved managed package, so
    /// a malformed one would silently fail to match rather than error.
    /// </summary>
    [Fact]
    public async Task Any_product_code_collected_is_a_well_formed_guid()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        foreach (var code in software.Select(s => s.ProductCode).Where(c => c is not null))
        {
            code!.ShouldStartWith("{");
            code.ShouldEndWith("}");
            Guid.TryParse(code, out _).ShouldBeTrue($"'{code}' is not a product code");
        }
    }

    /// <summary>
    /// The whole report is rejected by the Agent API if any field is over length,
    /// so the collector must never emit one - see SoftwareInventoryNormalizer.
    /// </summary>
    [Fact]
    public async Task No_entry_exceeds_the_wire_limits()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        software.Count.ShouldBeLessThan(8192);
        software.ShouldAllBe(s => s.Name.Length <= 384);
        software.ShouldAllBe(s => s.Version == null || s.Version.Length <= 128);
        software.ShouldAllBe(s => s.Publisher == null || s.Publisher.Length <= 256);
        software.ShouldAllBe(s => s.InstallLocation == null || s.InstallLocation.Length <= 512);
    }

    /// <summary>
    /// Any install location reported is one Force Stop could actually act on.
    /// </summary>
    /// <remarks>
    /// From 1.7.0 the collector recovers a location for applications whose
    /// uninstall key omits one. The value it publishes becomes the root the
    /// endpoint terminates processes under, so the invariant that matters is not
    /// how many are recovered -- that is a property of the machine -- but that
    /// nothing unusable is ever published. A location the matcher would refuse
    /// would show in the console as resolved while every Force Stop against it
    /// failed.
    /// </remarks>
    [Fact]
    public async Task Every_reported_install_location_is_one_the_matcher_accepts()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        var unusable = software
            .Where(s => s.InstallLocation is not null)
            .Where(s => !EndpointAgent.Core.Inventory.ApplicationProcessMatcher.CanResolve(s.InstallLocation))
            .Select(s => $"{s.Name} -> {s.InstallLocation}")
            .ToList();

        unusable.ShouldBeEmpty();
    }

    /// <remarks>
    /// The agent resolves like any other installed application, and its product
    /// code genuinely maps to its own directory. Publishing that would offer an
    /// operator a Force Stop on the agent itself.
    /// <para>
    /// Weak where it runs: under a test host "self" is the test output directory,
    /// which no product installs into, so this passes without exercising much. It
    /// is the production invariant stated in the place it applies -- the guard
    /// itself is proven against a fixed directory, and by mutation, in
    /// <c>WindowsInstallLocationResolverTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Never_reports_the_agents_own_directory_as_an_install_location()
    {
        var software = await Create().CollectAsync(CancellationToken.None);
        var self = AppContext.BaseDirectory.TrimEnd('\\');

        software
            .Where(s => s.InstallLocation is not null)
            .ShouldAllBe(s => !self.StartsWith(s.InstallLocation!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_field_name_resembles_credential_material()
    {
        // Sanity on the contract shape - software inventory carries no secrets.
        foreach (var p in typeof(EndpointPlatform.Contracts.Agent.InventorySoftware).GetProperties())
        {
            p.Name.ShouldNotContain("Password");
            p.Name.ShouldNotContain("Secret");
        }
        await Task.CompletedTask;
    }
}
