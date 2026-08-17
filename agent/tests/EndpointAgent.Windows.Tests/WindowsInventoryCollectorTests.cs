using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests: runs the real collector against this machine's WMI
/// and network stack, asserting shape invariants rather than machine-specific
/// values.
/// </summary>
public sealed class WindowsInventoryCollectorTests
{
    private static WindowsInventoryCollector CreateCollector() =>
        new(
            new WindowsSystemInfoProvider(NullLogger<WindowsSystemInfoProvider>.Instance),
            TimeProvider.System,
            NullLogger<WindowsInventoryCollector>.Instance);

    [Fact]
    public async Task Collects_a_complete_report_without_throwing()
    {
        var report = await CreateCollector().CollectAsync(CancellationToken.None);

        report.ShouldNotBeNull();
        report.Hardware.ShouldNotBeNull();
        report.NetworkInterfaces.ShouldNotBeNull();
        report.CollectedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Reports_plausible_hardware_facts_for_this_machine()
    {
        var hardware = (await CreateCollector().CollectAsync(CancellationToken.None)).Hardware;

        // Any real Windows machine has a CPU, RAM and at least one fixed volume.
        hardware.CpuName.ShouldNotBeNullOrWhiteSpace();
        hardware.CpuLogicalProcessors.ShouldNotBeNull();
        hardware.CpuLogicalProcessors.Value.ShouldBe(Environment.ProcessorCount);
        hardware.TotalMemoryBytes.ShouldNotBeNull();
        hardware.TotalMemoryBytes.Value.ShouldBeGreaterThan(1_000_000_000);
        hardware.Disks.ShouldNotBeEmpty();
        hardware.Disks.All(d => d.SizeBytes > 0).ShouldBeTrue();
        hardware.Disks.All(d => d.FreeBytes <= d.SizeBytes).ShouldBeTrue();
    }

    [Fact]
    public async Task Reports_at_least_one_network_interface_and_no_loopback()
    {
        var interfaces = (await CreateCollector().CollectAsync(CancellationToken.None)).NetworkInterfaces;

        interfaces.ShouldNotBeEmpty();
        interfaces.ShouldAllBe(n => !string.IsNullOrWhiteSpace(n.Name));
        interfaces.ShouldAllBe(n => !n.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Interface_addresses_are_parseable_ips()
    {
        var interfaces = (await CreateCollector().CollectAsync(CancellationToken.None)).NetworkInterfaces;

        foreach (var address in interfaces.SelectMany(n => n.IpAddresses))
        {
            System.Net.IPAddress.TryParse(address, out _).ShouldBeTrue(
                $"'{address}' should be a parseable IP address");
        }
    }
}
