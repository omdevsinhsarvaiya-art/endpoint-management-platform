using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// Running state on the installations drill-down: judged from the last
/// inventory's evidence, tri-state, and filterable without the count and the
/// pages disagreeing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SoftwareReadServiceRunningTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private const string Title = "Slack";
    private const string Version = "4.52";
    private const string Publisher = "Slack Technologies Inc.";
    private const string SlackRoot = @"C:\Program Files\WindowsApps\slack";

    /// <summary>
    /// One title on three devices: running, stopped, and reported by an agent
    /// too old to say either.
    /// </summary>
    private async Task<(Organization Org, Device Running, Device Stopped, Device Legacy)> SeedAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var slug = ("rn" + Guid.CreateVersion7().ToString("N")).Substring(0, 20);
        var org = new Organization("Running Org", slug);
        db.Organizations.Add(org);

        var now = DateTimeOffset.UtcNow;
        var token = new Domain.Enrollment.EnrollmentToken(
            org.Id, "t", Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", now.AddHours(1), 5);
        db.EnrollmentTokens.Add(token);

        var running = Device.Enroll(org.Id, "RUN-A", "m-" + Guid.CreateVersion7().ToString("N"), "1.9.0", null, token.Id, now);
        var stopped = Device.Enroll(org.Id, "RUN-B", "m-" + Guid.CreateVersion7().ToString("N"), "1.9.0", null, token.Id, now);
        var legacy = Device.Enroll(org.Id, "RUN-C", "m-" + Guid.CreateVersion7().ToString("N"), "1.5.0", null, token.Id, now);
        db.Devices.AddRange(running, stopped, legacy);

        DeviceSoftware Row(Guid deviceId) =>
            new(deviceId, Title, Version, Publisher, null, SlackRoot, null, now, "User", "PC\\alice", null,
                "Package", "com.tinyspeck.slackdesktop_8yrtsj140pw4g", null, "Installed", "Application");

        var runningRow = Row(running.Id);
        var stoppedRow = Row(stopped.Id);
        db.DeviceSoftware.AddRange(runningRow, stoppedRow, Row(legacy.Id));

        db.DeviceSoftwareEvidence.AddRange(
            new DeviceSoftwareEvidence(runningRow.Id, 0, "PackageRegistration", "Slack", null),
            new DeviceSoftwareEvidence(runningRow.Id, 1, DeviceSoftwareEvidence.RunningProcessSource, "slack", SlackRoot + @"\app\Slack.exe"),
            // Witnessed, but by nothing that was running.
            new DeviceSoftwareEvidence(stoppedRow.Id, 0, "PackageRegistration", "Slack", null),
            new DeviceSoftwareEvidence(stoppedRow.Id, 1, "StartMenuShortcut", "Slack", SlackRoot + @"\app\Slack.exe"));

        await db.SaveChangesAsync();
        return (org, running, stopped, legacy);
    }

    private static Task<SoftwareInstallationPage> ListAsync(
        SoftwareReadService service, Guid organizationId, SoftwareRunningFilter running) =>
        service.ListInstallationsAsync(organizationId, null, Title, Version, Publisher, 1, 50, running, CancellationToken.None);

    [Fact]
    public async Task Running_state_is_true_false_or_unknown_from_the_last_inventory()
    {
        var (org, running, stopped, legacy) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var page = await ListAsync(service, org.Id, SoftwareRunningFilter.All);

        page.TotalCount.ShouldBe(3);
        page.Items.Single(i => i.DeviceId == running.Id).IsRunning.ShouldBe(true);
        page.Items.Single(i => i.DeviceId == stopped.Id).IsRunning.ShouldBe(false);
        // No evidence at all is an older agent, and an older agent said nothing.
        page.Items.Single(i => i.DeviceId == legacy.Id).IsRunning.ShouldBeNull();
    }

    [Fact]
    public async Task The_running_filter_keeps_only_rows_seen_running()
    {
        var (org, running, _, _) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var page = await ListAsync(service, org.Id, SoftwareRunningFilter.Running);

        page.TotalCount.ShouldBe(1);
        page.Items.ShouldHaveSingleItem().DeviceId.ShouldBe(running.Id);
    }

    /// <summary>
    /// "Stopped" is a claim, and it needs evidence to make. An older agent's row
    /// is not stopped -- it is unknown, and unknown appears under All only.
    /// </summary>
    [Fact]
    public async Task The_stopped_filter_excludes_rows_with_no_evidence_at_all()
    {
        var (org, _, stopped, _) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var page = await ListAsync(service, org.Id, SoftwareRunningFilter.Stopped);

        page.TotalCount.ShouldBe(1);
        page.Items.ShouldHaveSingleItem().DeviceId.ShouldBe(stopped.Id);
    }

    /// <summary>Callers that never asked about running state see everything, as before.</summary>
    [Fact]
    public async Task The_existing_overload_applies_no_filter()
    {
        var (org, _, _, _) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var page = await service.ListInstallationsAsync(org.Id, null, Title, Version, Publisher, 1, 50, CancellationToken.None);

        page.TotalCount.ShouldBe(3);
        page.Items.Count.ShouldBe(3);
    }
}
