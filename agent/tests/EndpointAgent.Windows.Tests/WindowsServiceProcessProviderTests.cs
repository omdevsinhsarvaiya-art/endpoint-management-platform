using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>Read-only integration against this machine's real services and processes.</summary>
public sealed class WindowsServiceProcessProviderTests
{
    private static WindowsServiceProcessProvider Create() =>
        new(NullLogger<WindowsServiceProcessProvider>.Instance);

    [Fact]
    public async Task Lists_services_including_a_well_known_one()
    {
        var services = await Create().CollectServicesAsync(CancellationToken.None);
        services.ShouldNotBeEmpty();
        // The Windows Event Log service exists on every Windows machine.
        services.ShouldContain(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
        services.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Status));
    }

    [Fact]
    public async Task Lists_top_processes_capped_and_sorted()
    {
        var processes = await Create().CollectProcessesAsync(10, CancellationToken.None);
        processes.Count.ShouldBeLessThanOrEqualTo(10);
        processes.ShouldNotBeEmpty();
        // Sorted by working set descending.
        for (var i = 1; i < processes.Count; i++)
        {
            processes[i].WorkingSetBytes.ShouldBeLessThanOrEqualTo(processes[i - 1].WorkingSetBytes);
        }
    }

    [Fact]
    public async Task Terminating_a_system_process_is_refused()
    {
        var control = Create();
        await Should.ThrowAsync<InvalidOperationException>(
            () => control.TerminateProcessAsync(4, "System", CancellationToken.None));
    }
}
