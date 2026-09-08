using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// The fleet-wide software views after application discovery: what the default
/// view hides, what an explicit request shows, and that a row an older agent
/// reported -- with no category to hide it by -- is shown in both.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SoftwareReadServiceDiscoveryTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private async Task<(Organization Org, Device Device)> SeedAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var slug = ("dv" + Guid.CreateVersion7().ToString("N")).Substring(0, 20);
        var org = new Organization("Discovery Org", slug);
        db.Organizations.Add(org);

        var now = DateTimeOffset.UtcNow;
        // A unique secret hash per seed: the column is unique across the shared test database.
        var token = new Domain.Enrollment.EnrollmentToken(
            org.Id, "t", Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", now.AddHours(1), 5);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(org.Id, "DISC1", "m-" + Guid.CreateVersion7().ToString("N"), "1.9.0", null, token.Id, now);
        db.Devices.Add(device);

        db.DeviceSoftware.AddRange(
            new DeviceSoftware(device.Id, "Slack", "4.52", "Slack Technologies Inc.", null, @"C:\Program Files\WindowsApps\slack", null, now,
                "User", "PC\\alice", null, "Package", "com.tinyspeck.slackdesktop_8yrtsj140pw4g", null, "Installed", "Application",
                "com.tinyspeck.slackdesktop_8yrtsj140pw4g", null, null, @"C:\Program Files\WindowsApps\slack\app\Slack.exe",
                "CN=Slack Technologies, LLC", "Signed"),
            new DeviceSoftware(device.Id, "Microsoft.VCLibs.140.00", "14.0", "Microsoft Corporation", null, @"C:\Program Files\WindowsApps\vclibs", null, now,
                "User", "PC\\alice", null, "Package", "Microsoft.VCLibs.140.00_8wekyb3d8bbwe", null, "Installed", "FrameworkOrResource"),
            new DeviceSoftware(device.Id, "Settings", "10.0", "Microsoft Windows", null, @"C:\Windows\ImmersiveControlPanel", null, now,
                "Machine", null, null, "Package", "windows.immersivecontrolpanel_cw5n1h2txyewy", null, "Installed", "InboxApp"),
            new DeviceSoftware(device.Id, "Caffeine", "1.98", "Zhorn Software", null, null, null, now,
                "User", "PC\\alice", null, "Executable", "zhorn\u001fcaffeine\u001fdownloads", null, "Observed", "Observed",
                null, null, null, @"C:\Users\alice\Downloads\caffeine64.exe", null, "Unsigned"),
            // An older agent's row: no category, no confidence.
            new DeviceSoftware(device.Id, "7-Zip", "23.01", "Igor Pavlov", null, null, "x64", now, "Machine"));
        await db.SaveChangesAsync();

        return (org, device);
    }

    [Fact]
    public async Task The_applications_view_hides_frameworks_and_inbox_apps_but_not_older_rows()
    {
        var (org, _) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var applications = await service.ListTitlesAsync(org.Id, 1, 50, null, null, SoftwareView.Applications, CancellationToken.None);

        applications.Items.Select(t => t.Name).ShouldBe(["7-Zip", "Caffeine", "Slack"], ignoreOrder: true);
        applications.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task The_all_view_shows_everything_and_the_default_is_applications()
    {
        var (org, _) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var all = await service.ListTitlesAsync(org.Id, 1, 50, null, null, SoftwareView.All, CancellationToken.None);
        var byDefault = await service.ListTitlesAsync(org.Id, 1, 50, null, null, CancellationToken.None);

        all.TotalCount.ShouldBe(5);
        all.Items.Select(t => t.Name).ShouldContain("Microsoft.VCLibs.140.00");
        all.Items.Select(t => t.Name).ShouldContain("Settings");
        byDefault.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task A_title_carries_its_category_and_confidence()
    {
        var (org, _) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var all = await service.ListTitlesAsync(org.Id, 1, 50, null, null, SoftwareView.All, CancellationToken.None);

        all.Items.Single(t => t.Name == "Slack").Category.ShouldBe("Application");
        all.Items.Single(t => t.Name == "Slack").Confidence.ShouldBe("Installed");
        all.Items.Single(t => t.Name == "Caffeine").Confidence.ShouldBe("Observed");
        all.Items.Single(t => t.Name == "Caffeine").Category.ShouldBe("Observed");
        all.Items.Single(t => t.Name == "Settings").Category.ShouldBe("InboxApp");
        all.Items.Single(t => t.Name == "7-Zip").Category.ShouldBeNull();
        all.Items.Single(t => t.Name == "7-Zip").Confidence.ShouldBeNull();
    }

    [Fact]
    public async Task An_installation_carries_the_discovery_fields()
    {
        var (org, device) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var service = new SoftwareReadService(db);

        var slack = (await service.ListInstallationsAsync(org.Id, null, "Slack", "4.52", "Slack Technologies Inc.", 1, 50, CancellationToken.None))
            .Items.ShouldHaveSingleItem();

        slack.DeviceId.ShouldBe(device.Id);
        slack.IdentityKind.ShouldBe("Package");
        slack.StableKey.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        slack.Confidence.ShouldBe("Installed");
        slack.Category.ShouldBe("Application");
        slack.PackageFamilyName.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        slack.ExecutablePath.ShouldEndWith(@"\app\Slack.exe");
        slack.SignerSubject.ShouldBe("CN=Slack Technologies, LLC");
        slack.SignatureStatus.ShouldBe("Signed");
        slack.InstallLocation.ShouldBe(@"C:\Program Files\WindowsApps\slack");

        var caffeine = (await service.ListInstallationsAsync(org.Id, null, "Caffeine", "1.98", "Zhorn Software", 1, 50, CancellationToken.None))
            .Items.ShouldHaveSingleItem();
        caffeine.Confidence.ShouldBe("Observed");
        caffeine.InstallLocation.ShouldBeNull();
        caffeine.SignatureStatus.ShouldBe("Unsigned");
    }

    /// <summary>
    /// A title's rows normally agree on category and confidence. Where they do
    /// not, the strongest claim wins: an Installed row outranks an Observed one.
    /// </summary>
    [Fact]
    public async Task A_title_installed_on_one_device_and_only_observed_on_another_reads_as_installed()
    {
        var (org, device) = await SeedAsync();
        await using var db = _fixture.CreateDbContext();
        var token = await db.EnrollmentTokens.FirstAsync(t => t.OrganizationId == org.Id);
        var other = Device.Enroll(org.Id, "DISC2", "m-" + Guid.CreateVersion7().ToString("N"), "1.9.0", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(other);
        db.DeviceSoftware.Add(new DeviceSoftware(other.Id, "Caffeine", "1.98", "Zhorn Software", null, @"C:\Tools\Caffeine", null, DateTimeOffset.UtcNow,
            "Machine", null, null, "Registered", "zhorn\u001fcaffeine\u001fc:\\tools\\caffeine", null, "Installed", "Application"));
        await db.SaveChangesAsync();

        var service = new SoftwareReadService(db);
        var caffeine = (await service.ListTitlesAsync(org.Id, 1, 50, "Caffeine", null, SoftwareView.All, CancellationToken.None))
            .Items.ShouldHaveSingleItem();

        caffeine.InstallCount.ShouldBe(2);
        caffeine.Confidence.ShouldBe("Installed");
        caffeine.Category.ShouldBe("Application");
        _ = device;
    }
}
