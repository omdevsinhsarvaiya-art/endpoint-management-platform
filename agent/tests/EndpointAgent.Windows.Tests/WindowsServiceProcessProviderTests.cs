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

    /// <summary>
    /// The complete enumeration is the whole process table, not a summary of it.
    /// Processes start and exit while it runs, so the count is held to the band
    /// the OS reported immediately before and after, not to an exact number.
    /// </summary>
    [Fact]
    public async Task Collects_every_process_the_os_reports()
    {
        var before = System.Diagnostics.Process.GetProcesses().Length;
        var all = await Create().CollectAllProcessesAsync(CancellationToken.None);
        var after = System.Diagnostics.Process.GetProcesses().Length;

        all.Count.ShouldBeInRange(Math.Min(before, after) - 25, Math.Max(before, after) + 25);
        all.Select(p => p.ProcessId).ShouldBeUnique();
        all.ShouldContain(p => p.ProcessId == Environment.ProcessId, "the test host itself is a running process");
    }

    /// <summary>
    /// The regression itself, and the only test here that can tell the two
    /// methods apart: above 500 processes the summary stops and the complete
    /// enumeration does not. Reported as skipped, with the reason, below that.
    /// </summary>
    [ManyProcessesFact]
    public async Task Collects_more_than_the_inventory_cap_when_the_machine_runs_more()
    {
        var provider = Create();

        var all = await provider.CollectAllProcessesAsync(CancellationToken.None);
        var summary = await provider.CollectProcessesAsync(10_000, CancellationToken.None);

        all.Count.ShouldBeGreaterThan(ManyProcessesFactAttribute.Threshold);
        summary.Count.ShouldBe(500, "the inventory path stays capped however much is asked for");
    }

    [Fact]
    public async Task Terminating_a_system_process_is_refused()
    {
        var control = Create();
        await Should.ThrowAsync<InvalidOperationException>(
            () => control.TerminateProcessAsync(4, "System", CancellationToken.None));
    }
}
