using EndpointAgent.Core.Abstractions;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests for BitLocker collection, run against this machine.
/// </summary>
/// <remarks>
/// <para>
/// The test host is usually unelevated, so the expected outcome here is
/// <c>AccessDenied</c> with no volumes — which is precisely the case that must not be
/// mistaken for an unencrypted machine. These tests therefore assert the shape of
/// every outcome rather than any particular one, and assert hardest on what must
/// never happen: a failed query producing volume rows, or any value resembling a
/// recovery key.
/// </para>
/// <para>
/// The collector is the security posture collector, deliberately. One class reads
/// <c>Win32_EncryptableVolume</c>, so the single-field posture summary and the
/// per-volume detail cannot disagree about the same machine.
/// </para>
/// </remarks>
public sealed class WindowsBitLockerCollectorTests
{
    private static WindowsSecurityPostureCollector Collector() =>
        new(
            new WindowsLocalAccountsCollector(NullLogger<WindowsLocalAccountsCollector>.Instance),
            NullLogger<WindowsSecurityPostureCollector>.Instance);

    [Fact]
    public async Task Collects_without_throwing_whatever_this_machine_allows()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        result.ShouldNotBeNull();
        result.Volumes.ShouldNotBeNull();
    }

    /// <summary>
    /// The status is a closed set the server knows how to parse. An unrecognised
    /// value would be stored as Unknown, which is safe, but it would also silently
    /// discard every volume — so the vocabulary must match.
    /// </summary>
    [Fact]
    public async Task Reports_one_of_the_statuses_the_server_understands()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        result.Status.ShouldBeOneOf("Available", "AccessDenied", "NotAvailable", "Error");
    }

    /// <summary>
    /// The invariant the whole availability field exists to protect. A query that did
    /// not succeed must carry no volumes, so the server cannot mistake an empty list
    /// from a refused query for a machine with nothing encrypted.
    /// </summary>
    [Fact]
    public async Task A_failed_query_reports_no_volumes()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        if (result.Status != "Available")
        {
            result.Volumes.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Every_reported_volume_has_an_identity()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        result.Volumes.All(v => !string.IsNullOrWhiteSpace(v.DeviceIdentifier)).ShouldBeTrue();
    }

    [Fact]
    public async Task Reported_values_are_within_the_documented_ranges()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        foreach (var volume in result.Volumes)
        {
            if (volume.EncryptionPercentage is { } percentage)
            {
                percentage.ShouldBeInRange(0, 100);
            }

            if (volume.ConversionStatus is { } conversion)
            {
                conversion.ShouldBeInRange(0, 5);
            }

            if (volume.ProtectionStatus is { } protection)
            {
                protection.ShouldBeInRange(0, 2);
            }
        }
    }

    /// <summary>
    /// The central security property of this milestone, asserted against the live
    /// collector: nothing it produces on a real machine has the shape of a recovery
    /// password. The agent never calls the method that returns one.
    /// </summary>
    [Fact]
    public async Task Nothing_collected_resembles_a_recovery_key()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        foreach (var volume in result.Volumes)
        {
            var values = new List<string?>
            {
                volume.DeviceIdentifier, volume.DriveLetter, volume.PersistentVolumeId,
            };

            values.AddRange(volume.RecoveryProtectorIds ?? []);

            foreach (var value in values.Where(v => !string.IsNullOrEmpty(v)))
            {
                System.Text.RegularExpressions.Regex.IsMatch(value!, @"\d{6}-\d{6}")
                    .ShouldBeFalse("a recovery-password shape was collected");
            }
        }
    }

    /// <summary>
    /// Protector identifiers are GUIDs. If this ever failed, the collector would be
    /// returning something other than the identifier list — the first sign of reading
    /// the wrong output property.
    /// </summary>
    [Fact]
    public async Task Protector_identifiers_are_guids()
    {
        var result = await Collector().CollectBitLockerAsync(CancellationToken.None);

        foreach (var id in result.Volumes.SelectMany(v => v.RecoveryProtectorIds ?? []))
        {
            Guid.TryParse(id.Trim().Trim('{', '}'), out _)
                .ShouldBeTrue($"protector id '{id}' is not a GUID");
        }
    }

    /// <summary>
    /// The posture's single-field BitLocker summary and the volume list come from one
    /// read, so they cannot contradict each other. When the query is refused, the
    /// summary must be null — the "unknown" the compliance score excludes rather than
    /// scores as a failure.
    /// </summary>
    [Fact]
    public async Task The_posture_summary_agrees_with_the_volume_detail()
    {
        var collector = Collector();

        var posture = await collector.CollectAsync(CancellationToken.None);
        var bitLocker = await collector.CollectBitLockerAsync(CancellationToken.None);

        if (bitLocker.Status != "Available")
        {
            posture.BitLockerSystemDriveStatus.ShouldBeNull(
                "an unreadable query must leave the compliance input unknown, not failed");
            return;
        }

        posture.BitLockerSystemDriveStatus.ShouldBeOneOf(null, "On", "Off", "Unknown");
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Collector().CollectBitLockerAsync(cts.Token));
    }

    /// <summary>
    /// The collector is reachable through the interface the inventory pipeline uses,
    /// which is what proves the explicit implementation is wired rather than merely
    /// present.
    /// </summary>
    [Fact]
    public async Task Is_usable_through_the_bitlocker_collector_interface()
    {
        IBitLockerCollector collector = Collector();

        (await collector.CollectAsync(CancellationToken.None)).ShouldNotBeNull();
    }
}
