using EndpointAgent.Core.Inventory;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The App Paths execution aliases, read from this machine's real registry.
/// </summary>
/// <remarks>
/// Asserts the shape every real machine satisfies, not any particular
/// application: aliases exist on every Windows install, every one the source
/// reports is an absolute local path, and none is the kind of value discovery
/// refuses. What an alias is worth -- a reference, never an installation -- is
/// decided by the merger and tested with fixtures.
/// </remarks>
public sealed class WindowsAppPathsEvidenceSourceTests
{
    private static WindowsAppPathsEvidenceSource Create() =>
        new(NullLogger<WindowsAppPathsEvidenceSource>.Instance);

    [Fact]
    public async Task Reports_aliases_from_the_machine_registry()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        // Windows itself registers several machine-wide aliases.
        evidence.ShouldContain(e => e.Scope == SoftwareScope.Machine);
    }

    [Fact]
    public async Task Every_alias_is_an_absolute_local_executable_path()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => e.Source == EvidenceSource.AppPaths);
        evidence.ShouldAllBe(e => e.ExecutablePath != null && ExecutablePath.Normalize(e.ExecutablePath) == e.ExecutablePath);
        evidence.ShouldAllBe(e => !e.ExecutablePath!.Contains("..", StringComparison.Ordinal));
    }

    /// <summary>An alias is a pointer, and carries no claim about what it points at.</summary>
    [Fact]
    public async Task An_alias_never_claims_to_be_an_installation()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => !e.IsAuthoritative);
        evidence.ShouldAllBe(e => e.ProductCode == null && e.PackageFamilyName == null);
    }

    /// <summary>A per-user alias is attributed; a machine alias is not.</summary>
    [Fact]
    public async Task User_aliases_carry_their_account_and_machine_aliases_do_not()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.Where(e => e.Scope == SoftwareScope.Machine).ShouldAllBe(e => e.InstalledForUser == null);
        evidence.Where(e => e.Scope == SoftwareScope.User).ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.InstalledForUser));
    }

    /// <summary>
    /// Through the whole pipeline, aliases alone put nothing in the report: a
    /// pointer is not software, and a stale one must not become a row.
    /// </summary>
    [Fact]
    public async Task Aliases_alone_produce_no_inventory_rows()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        SoftwareDiscoveryPipeline.Run(evidence).ShouldBeEmpty();
    }
}
