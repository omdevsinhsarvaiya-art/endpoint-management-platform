using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Running state as the console sees it: a tri-state <c>isRunning</c> on the
/// installations drill-down and on the device page, and a filter on the
/// drill-down -- all judged from the last inventory's evidence, never live.
/// </summary>
/// <remarks>
/// Tri-state is the substance. An agent older than 1.9.0 reports no evidence,
/// so its rows are neither running nor stopped; a "Not running" filter that
/// listed them would assert something the platform never determined.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class SoftwareRunningStateTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private const string SlackRoot = @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";

    private sealed record InstallationRow(Guid DeviceId, string Hostname, bool? IsRunning);

    private sealed record InstallationPage(IReadOnlyList<InstallationRow> Items, int TotalCount);

    private sealed record SoftwareItem(string Name, bool? IsRunning);

    private sealed record DeviceDetail(Guid Id, IReadOnlyList<SoftwareItem> Software);

    /// <summary>
    /// One title, unique to this seed, on three devices: seen running, seen but
    /// not running, and reported by an agent too old to say.
    /// </summary>
    private async Task<(string Title, Guid Running, Guid Stopped, Guid Legacy)> SeedAsync()
    {
        var title = $"Slack {Guid.CreateVersion7():N}"[..24];

        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"run-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "running-state-tests", now.AddHours(1), 3);
        db.EnrollmentTokens.Add(token);

        Device Enroll(string prefix, string agentVersion) =>
            Device.Enroll(organizationId, $"{prefix}-{Guid.CreateVersion7():N}"[..12], $"smbios-{Guid.CreateVersion7()}",
                agentVersion, "Microsoft Windows 11 Pro", token.Id, now);

        var running = Enroll("RUN", "1.9.0");
        var stopped = Enroll("STP", "1.9.0");
        var legacy = Enroll("OLD", "1.5.0");
        db.Devices.AddRange(running, stopped, legacy);

        DeviceSoftware Row(Guid deviceId) =>
            new(deviceId, title, "4.52.155.0", "Slack Technologies", null, SlackRoot, null, now,
                "User", @"CORP\alice", null, "Package", "com.tinyspeck.slackdesktop_8yrtsj140pw4g", null,
                "Installed", "Application");

        var runningRow = Row(running.Id);
        var stoppedRow = Row(stopped.Id);
        db.DeviceSoftware.AddRange(runningRow, stoppedRow, Row(legacy.Id));

        db.DeviceSoftwareEvidence.AddRange(
            new DeviceSoftwareEvidence(runningRow.Id, 0, "PackageRegistration", "Slack", null),
            new DeviceSoftwareEvidence(runningRow.Id, 1, "StartMenuShortcut", "Slack", SlackRoot + @"\app\Slack.exe"),
            new DeviceSoftwareEvidence(runningRow.Id, 2, "RunningProcess", "slack", SlackRoot + @"\app\Slack.exe"),
            new DeviceSoftwareEvidence(stoppedRow.Id, 0, "PackageRegistration", "Slack", null),
            new DeviceSoftwareEvidence(stoppedRow.Id, 1, "StartMenuShortcut", "Slack", SlackRoot + @"\app\Slack.exe"));

        await db.SaveChangesAsync();
        return (title, running.Id, stopped.Id, legacy.Id);
    }

    private async Task<HttpClient> ClientAsync()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        return _fixture.CreateClientFor(token);
    }

    private static Uri Installations(string title, string? running = null) =>
        new($"/admin/v1/software/installations?name={Uri.EscapeDataString(title)}&version=4.52.155.0"
            + $"&publisher={Uri.EscapeDataString("Slack Technologies")}"
            + (running is null ? "" : $"&running={running}"), UriKind.Relative);

    [Fact]
    public async Task Installations_carry_the_running_state_as_of_the_last_inventory()
    {
        var (title, running, stopped, legacy) = await SeedAsync();
        using var client = await ClientAsync();

        var page = (await client.GetFromJsonAsync<InstallationPage>(Installations(title)))!;

        page.TotalCount.ShouldBe(3);
        page.Items.Single(i => i.DeviceId == running).IsRunning.ShouldBe(true);
        page.Items.Single(i => i.DeviceId == stopped).IsRunning.ShouldBe(false);
        page.Items.Single(i => i.DeviceId == legacy).IsRunning.ShouldBeNull();
    }

    /// <summary>
    /// The filter is applied before the count, so the total an operator reads is
    /// the number of rows the filter kept -- and unknown rows appear under All only.
    /// </summary>
    [Fact]
    public async Task The_running_filter_narrows_the_rows_and_the_total()
    {
        var (title, running, stopped, _) = await SeedAsync();
        using var client = await ClientAsync();

        var seenRunning = (await client.GetFromJsonAsync<InstallationPage>(Installations(title, "running")))!;
        seenRunning.TotalCount.ShouldBe(1);
        seenRunning.Items.ShouldHaveSingleItem().DeviceId.ShouldBe(running);

        var seenStopped = (await client.GetFromJsonAsync<InstallationPage>(Installations(title, "stopped")))!;
        seenStopped.TotalCount.ShouldBe(1);
        seenStopped.Items.ShouldHaveSingleItem().DeviceId.ShouldBe(stopped);

        var all = (await client.GetFromJsonAsync<InstallationPage>(Installations(title, "all")))!;
        all.TotalCount.ShouldBe(3);
        all.Items.Count.ShouldBe(3);
    }

    [Fact]
    public async Task An_unknown_running_filter_is_refused()
    {
        var (title, _, _, _) = await SeedAsync();
        using var client = await ClientAsync();

        var response = await client.GetAsync(Installations(title, "maybe"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The device page computes the same answer from the evidence it already
    /// carries, so the two views can never disagree about one row.
    /// </summary>
    [Fact]
    public async Task The_device_page_reports_running_state_per_row()
    {
        var (title, running, stopped, legacy) = await SeedAsync();
        using var client = await ClientAsync();

        async Task<bool?> IsRunningOnAsync(Guid deviceId)
        {
            var detail = (await client.GetFromJsonAsync<DeviceDetail>(
                new Uri($"/admin/v1/devices/{deviceId}", UriKind.Relative)))!;
            return detail.Software.Single(s => s.Name == title).IsRunning;
        }

        (await IsRunningOnAsync(running)).ShouldBe(true);
        (await IsRunningOnAsync(stopped)).ShouldBe(false);
        (await IsRunningOnAsync(legacy)).ShouldBeNull();
    }
}
