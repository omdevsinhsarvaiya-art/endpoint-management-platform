using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration test against this machine's real uninstall registry.
/// Read-only; asserts invariants any real machine satisfies.
/// </summary>
public sealed class WindowsSoftwareCollectorTests
{
    private static WindowsSoftwareCollector Create() =>
        new(NullLogger<WindowsSoftwareCollector>.Instance);

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
    public async Task Entries_are_deduplicated_by_name_and_version()
    {
        var software = await Create().CollectAsync(CancellationToken.None);

        var keys = software.Select(s => $"{s.Name}|{s.Version}").ToList();
        keys.Distinct().Count().ShouldBe(keys.Count);
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
