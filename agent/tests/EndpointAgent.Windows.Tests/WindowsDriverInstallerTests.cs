using EndpointAgent.Core.Abstractions;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows tests for the driver installer, covering everything that can be exercised
/// without changing this machine's driver store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here installs a driver.</b> The tests drive the read-only matching path
/// and the refusal paths — the gates that decide, before the driver store is opened,
/// whether an installation may proceed at all. Actually binding a driver needs a real
/// signed package and a disposable machine, and belongs to the physical acceptance in
/// M13-6.
/// </para>
/// <para>
/// The matching test is the valuable one: it proves the pre-install hardware gate and
/// the post-install verification read the same devices the inventory collector does,
/// so a package refused for "no matching hardware" was judged against reality.
/// </para>
/// </remarks>
public sealed class WindowsDriverInstallerTests
{
    private static WindowsDriverInstaller Installer() =>
        new(NullLogger<WindowsDriverInstaller>.Instance);

    private static WindowsDriverCollector Collector() =>
        new(NullLogger<WindowsDriverCollector>.Instance);

    [Fact]
    public async Task An_unknown_hardware_id_matches_nothing()
    {
        var matches = await Installer().FindMatchingInstancesAsync(
            @"PCI\VEN_FFFF&DEV_FFFF&SUBSYS_FFFFFFFF", CancellationToken.None);

        matches.ShouldBeEmpty();
    }

    /// <summary>
    /// Takes a hardware id this machine genuinely has, from the inventory collector,
    /// and asserts the installer's matcher finds the same device. If these two
    /// disagreed, the hardware gate would refuse valid packages or admit wrong ones.
    /// </summary>
    [Fact]
    public async Task A_hardware_id_present_on_this_machine_matches_the_device_that_reports_it()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        // The collector reports instance ids; hardware ids come from the same
        // property store, so derive a real one through the installer itself by
        // walking a few well-known device stems this machine will have.
        var anyMatch = false;

        foreach (var candidate in await CandidateHardwareIdsAsync())
        {
            var matches = await Installer().FindMatchingInstancesAsync(candidate, CancellationToken.None);

            if (matches.Count > 0)
            {
                anyMatch = true;

                matches.All(m => !string.IsNullOrWhiteSpace(m.InstanceId)).ShouldBeTrue();

                // Every matched instance must be one the collector also knows about.
                foreach (var match in matches)
                {
                    drivers.Any(d => string.Equals(
                            d.InstanceId, match.InstanceId, StringComparison.OrdinalIgnoreCase))
                        .ShouldBeTrue(
                            $"the installer matched {match.InstanceId}, which the inventory does not report");
                }

                break;
            }
        }

        anyMatch.ShouldBeTrue("no hardware id on this machine matched, so the matcher may be broken");
    }

    /// <summary>
    /// Hardware ids are matched whole, not by substring. A device whose id merely
    /// contains the target's is a different device, and Windows would not bind the
    /// driver to it either.
    /// </summary>
    [Fact]
    public async Task A_partial_hardware_id_does_not_match()
    {
        var candidates = await CandidateHardwareIdsAsync();

        foreach (var candidate in candidates)
        {
            var matches = await Installer().FindMatchingInstancesAsync(candidate, CancellationToken.None);

            if (matches.Count == 0)
            {
                continue;
            }

            // A prefix of a real id must not match the device the full id does.
            var truncated = candidate[..(candidate.Length / 2)];

            (await Installer().FindMatchingInstancesAsync(truncated, CancellationToken.None))
                .ShouldBeEmpty($"'{truncated}' is a prefix, not a hardware id");

            return;
        }

        Assert.Fail("no hardware id on this machine matched, so the negative case could not be exercised");
    }

    /// <summary>
    /// An INF that is not signed by anything cannot pass the catalogue gate. Written
    /// to a temp directory, so nothing is staged and no device is touched.
    /// </summary>
    [Fact]
    public async Task An_unsigned_inf_is_refused_before_the_driver_store_is_touched()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"epa-drvinst-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var infPath = Path.Combine(directory, "fake.inf");
            await File.WriteAllTextAsync(
                infPath, "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=Net\r\n");

            var outcome = await Installer().InstallAsync(
                infPath, @"PCI\VEN_FFFF&DEV_FFFF", "Nobody In Particular",
                "1.0.0.0", "Nobody", CancellationToken.None);

            outcome.Succeeded.ShouldBeFalse();

            // Either the signature gate or the hardware gate refuses it. Both run
            // before anything is staged, which is what this asserts.
            outcome.Result.ShouldBeOneOf(
                DriverInstallResult.SignatureRejected,
                DriverInstallResult.SignerMismatch,
                DriverInstallResult.HardwareMismatch);

            outcome.Instances.ShouldBeEmpty();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Installer().FindMatchingInstancesAsync(@"PCI\VEN_8086&DEV_1234", cts.Token));
    }

    /// <summary>
    /// Hardware ids actually present on this machine, read the same way the installer
    /// reads them.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CandidateHardwareIdsAsync()
    {
        var drivers = await Collector().CollectAsync(CancellationToken.None);

        // Instance ids look like "PCI\VEN_8086&DEV_51BE&SUBSYS_...\3&11583659&0&E0".
        // The portion before the final backslash is the device's hardware stem, which
        // appears verbatim in its hardware-id list on essentially every PCI device.
        return drivers
            .Select(d => d.InstanceId)
            .Where(id => id.Contains('\\'))
            .Select(id => id[..id.LastIndexOf('\\')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }
}
