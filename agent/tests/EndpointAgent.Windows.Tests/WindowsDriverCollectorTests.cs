using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests: runs the real driver collector against this machine's
/// PnP tree, asserting shape invariants rather than machine-specific values.
/// </summary>
/// <remarks>
/// <para>
/// There is no way to assert which drivers a given test machine has, so these check
/// the things that must hold on any Windows box: every device has an identity, the
/// enumeration is bounded, present devices are the only ones reported, and nothing
/// the collector could not read is reported as a value.
/// </para>
/// <para>
/// The tests are also a signature check on the interop. Every P/Invoke here is a new
/// declaration, and a wrong <c>DEVPROPKEY</c> or a mismarshalled struct shows up as
/// a whole property being null across every device -- which is what
/// <see cref="Reads_driver_metadata_for_at_least_some_devices"/> exists to catch.
/// </para>
/// </remarks>
public sealed class WindowsDriverCollectorTests
{
    private static WindowsDriverCollector Collector() =>
        new(NullLogger<WindowsDriverCollector>.Instance);

    [Fact]
    public async Task Enumerates_this_machines_devices_without_throwing()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        drivers.ShouldNotBeNull();

        // Any real Windows machine has a processor, a disk and a display adapter.
        drivers.Count.ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task Every_device_has_an_identity_and_a_name()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        drivers.All(d => !string.IsNullOrWhiteSpace(d.InstanceId)).ShouldBeTrue();
        drivers.All(d => !string.IsNullOrWhiteSpace(d.DeviceName)).ShouldBeTrue();
    }

    /// <summary>
    /// The instance id is the devnode's identity and the key the server dedupes on.
    /// Duplicates here would mean the enumeration is walking something twice.
    /// </summary>
    [Fact]
    public async Task Instance_ids_are_unique_within_a_snapshot()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        drivers.Select(d => d.InstanceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(drivers.Count);
    }

    /// <summary>
    /// Proves the driver property keys actually resolve. If the DEVPROPKEY GUIDs or
    /// PIDs were wrong, every one of these would be null on every device and the
    /// feature would ship reporting nothing at all.
    /// </summary>
    [Fact]
    public async Task Reads_driver_metadata_for_at_least_some_devices()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        drivers.Any(d => !string.IsNullOrWhiteSpace(d.DriverVersion))
            .ShouldBeTrue("no device reported a driver version; DEVPKEY_Device_DriverVersion may be wrong");

        drivers.Any(d => !string.IsNullOrWhiteSpace(d.DriverProvider))
            .ShouldBeTrue("no device reported a driver provider; DEVPKEY_Device_DriverProvider may be wrong");

        drivers.Any(d => d.DriverDate is not null)
            .ShouldBeTrue("no device reported a driver date; the FILETIME property read may be wrong");

        drivers.Any(d => !string.IsNullOrWhiteSpace(d.InfName))
            .ShouldBeTrue("no device reported an INF; DEVPKEY_Device_DriverInfPath may be wrong");
    }

    /// <summary>
    /// A driver date that decodes wrongly is the classic FILETIME bug: the value
    /// lands on the 1601 epoch or in the far future, and nobody notices because it
    /// is only a display field.
    /// </summary>
    /// <remarks>
    /// The lower bound is 1601 and not something more recent on purpose. Real Intel
    /// chipset INFs on ordinary hardware carry a driver date of 18 July 1968 --
    /// verified against <c>Win32_PnPSignedDriver</c>, which reports the same value
    /// -- so an assertion that dates look modern fails on correct data. Only the
    /// FILETIME epoch itself indicates a decode fault.
    /// </remarks>
    [Fact]
    public async Task Driver_dates_are_plausible()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);
        var dates = drivers.Where(d => d.DriverDate is not null).Select(d => d.DriverDate!.Value).ToList();

        dates.ShouldNotBeEmpty();
        dates.All(d => d.Year > 1601).ShouldBeTrue("a driver date decoded onto the FILETIME epoch");
        dates.All(d => d <= DateTimeOffset.UtcNow.AddYears(2)).ShouldBeTrue("a driver date decoded in the future");
    }

    /// <summary>
    /// Problem codes are small CM_PROB_* values. A large or negative one would mean
    /// the status read is returning residue rather than a problem number.
    /// </summary>
    [Fact]
    public async Task Problem_codes_are_within_the_documented_range()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        drivers.Where(d => d.ProblemCode is not null)
            .All(d => d.ProblemCode >= 0 && d.ProblemCode < 100)
            .ShouldBeTrue();
    }

    /// <summary>
    /// A healthy machine is mostly healthy devices. If this failed, the status flags
    /// were being read as problems -- the bug that would have every endpoint in the
    /// estate report hundreds of phantom faults.
    /// </summary>
    [Fact]
    public async Task Most_devices_on_a_working_machine_report_no_problem()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);
        var healthy = drivers.Count(d => d.ProblemCode == 0);

        healthy.ShouldBeGreaterThan(drivers.Count / 2);
    }

    /// <summary>
    /// Signing is three-valued and every value is legitimate, so this asserts only
    /// that the check ran without turning an unanswerable question into "unsigned".
    /// A machine whose drivers are all Microsoft-signed should show some true.
    /// </summary>
    [Fact]
    public async Task Signature_state_is_read_without_defaulting_to_unsigned()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);
        var withInf = drivers.Where(d => !string.IsNullOrWhiteSpace(d.InfName)).ToList();

        withInf.ShouldNotBeEmpty();
        withInf.Any(d => d.IsSigned == true)
            .ShouldBeTrue("no INF verified; SetupVerifyInfFile may not be reached at all");
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Collector().CollectAsync(cts.Token));
    }
}
