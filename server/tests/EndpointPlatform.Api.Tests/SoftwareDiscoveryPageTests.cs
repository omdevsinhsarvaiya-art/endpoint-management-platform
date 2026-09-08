using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// What application discovery puts in front of an administrator: the device
/// page carries each row's identity, confidence, category, signer and evidence,
/// and the fleet-wide list hides frameworks and inbox apps unless asked.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class SoftwareDiscoveryPageTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private sealed record Evidence(string Source, string? Name, string? Detail);

    private sealed record SoftwareItem(
        string Name, string? Version, string? Publisher, string? InstallLocation,
        string? IdentityKind, string? StableKey, string? Confidence, string? Category,
        string? PackageFamilyName, string? ExecutablePath, string? SignerSubject, string? SignatureStatus,
        IReadOnlyList<Evidence> Evidence);

    private sealed record DeviceDetail(Guid Id, string Hostname, IReadOnlyList<SoftwareItem> Software);

    private sealed record Title(string Name, string? Version, string? Publisher, int InstallCount, string? Category, string? Confidence);

    private sealed record TitlePage(IReadOnlyList<Title> Items, int TotalCount);

    private const string SlackRoot = @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";

    /// <summary>A unique publisher per seed, so the fleet-wide list can be narrowed to this test's rows.</summary>
    private static string Publisher(string tag) => $"Discovery {tag} {Guid.CreateVersion7():N}"[..40];

    private async Task<(Guid DeviceId, string Publisher)> SeedAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;
        var publisher = Publisher("Corp");

        var token = new EnrollmentToken(
            organizationId, $"disc-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "discovery-tests", now.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, $"DSC-{Guid.CreateVersion7():N}"[..12], $"smbios-{Guid.CreateVersion7()}",
            "1.9.0", "Microsoft Windows 11 Pro", token.Id, now);
        db.Devices.Add(device);

        var slack = new DeviceSoftware(device.Id, "Slack", "4.52.155.0", publisher, null, SlackRoot, null, now,
            "User", @"OMDEVSINH-TECHS\Techsara", null,
            "Package", "com.tinyspeck.slackdesktop_8yrtsj140pw4g", "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g",
            "Installed", "Application", "com.tinyspeck.slackdesktop_8yrtsj140pw4g", "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g",
            null, SlackRoot + @"\app\Slack.exe", "CN=\"Slack Technologies, LLC\", O=\"Slack Technologies, LLC\"", "Signed");
        db.DeviceSoftware.Add(slack);
        db.DeviceSoftwareEvidence.AddRange(
            new DeviceSoftwareEvidence(slack.Id, 0, "PackageRegistration", "Slack", "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g"),
            new DeviceSoftwareEvidence(slack.Id, 1, "StartMenuShortcut", "Slack", SlackRoot + @"\app\Slack.exe"),
            new DeviceSoftwareEvidence(slack.Id, 2, "RunningProcess", "slack", SlackRoot + @"\app\Slack.exe"));

        db.DeviceSoftware.Add(new DeviceSoftware(device.Id, "Microsoft.VCLibs.140.00", "14.0.33728.0", publisher, null,
            @"C:\Program Files\WindowsApps\vclibs", null, now, "User", @"OMDEVSINH-TECHS\Techsara", null,
            "Package", "Microsoft.VCLibs.140.00_8wekyb3d8bbwe", null, "Installed", "FrameworkOrResource"));

        db.DeviceSoftware.Add(new DeviceSoftware(device.Id, "Caffeine", "1.98", publisher, null, null, null, now,
            "User", @"OMDEVSINH-TECHS\Techsara", null,
            "Executable", "zhorn\u001fcaffeine\u001fdownloads", null, "Observed", "Observed",
            null, null, null, @"C:\Users\Techsara\Downloads\caffeine64.exe", null, "Unsigned"));

        // An older agent's row: nothing discovery adds.
        db.DeviceSoftware.Add(new DeviceSoftware(device.Id, "7-Zip", "23.01", publisher, null, null, "x64", now, "Machine"));

        await db.SaveChangesAsync();
        return (device.Id, publisher);
    }

    private async Task<HttpClient> ClientAsync()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        return _fixture.CreateClientFor(token);
    }

    [Fact]
    public async Task The_device_page_carries_identity_confidence_category_signer_and_evidence()
    {
        var (deviceId, _) = await SeedAsync();
        using var client = await ClientAsync();

        var detail = (await client.GetFromJsonAsync<DeviceDetail>(new Uri($"/admin/v1/devices/{deviceId}", UriKind.Relative)))!;

        var slack = detail.Software.Single(s => s.Name == "Slack");
        slack.IdentityKind.ShouldBe("Package");
        slack.StableKey.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        slack.Confidence.ShouldBe("Installed");
        slack.Category.ShouldBe("Application");
        slack.PackageFamilyName.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        slack.ExecutablePath.ShouldEndWith(@"\app\Slack.exe");
        slack.SignerSubject.ShouldNotBeNull().ShouldContain("Slack Technologies");
        slack.SignatureStatus.ShouldBe("Signed");
        slack.InstallLocation.ShouldBe(SlackRoot);
        slack.Evidence.Select(e => e.Source).ShouldBe(["PackageRegistration", "StartMenuShortcut", "RunningProcess"]);
        slack.Evidence[0].Detail.ShouldBe("com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g");

        var caffeine = detail.Software.Single(s => s.Name == "Caffeine");
        caffeine.Confidence.ShouldBe("Observed");
        caffeine.InstallLocation.ShouldBeNull();
        caffeine.Evidence.ShouldBeEmpty();

        // The device page shows everything: hiding is the console's choice, made
        // against the category it can now see.
        detail.Software.Select(s => s.Name).ShouldContain("Microsoft.VCLibs.140.00");

        var sevenZip = detail.Software.Single(s => s.Name == "7-Zip");
        sevenZip.IdentityKind.ShouldBeNull();
        sevenZip.Confidence.ShouldBeNull();
        sevenZip.Category.ShouldBeNull();
        sevenZip.Evidence.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_fleet_list_hides_system_categories_by_default_and_shows_them_on_request()
    {
        var (_, publisher) = await SeedAsync();
        using var client = await ClientAsync();

        var byDefault = (await client.GetFromJsonAsync<TitlePage>(
            new Uri($"/admin/v1/software?publisher={Uri.EscapeDataString(publisher)}&pageSize=50", UriKind.Relative)))!;
        var all = (await client.GetFromJsonAsync<TitlePage>(
            new Uri($"/admin/v1/software?publisher={Uri.EscapeDataString(publisher)}&pageSize=50&view=all", UriKind.Relative)))!;
        var applications = (await client.GetFromJsonAsync<TitlePage>(
            new Uri($"/admin/v1/software?publisher={Uri.EscapeDataString(publisher)}&pageSize=50&view=applications", UriKind.Relative)))!;

        byDefault.Items.Select(t => t.Name).ShouldBe(["7-Zip", "Caffeine", "Slack"], ignoreOrder: true);
        applications.TotalCount.ShouldBe(byDefault.TotalCount);
        all.Items.Select(t => t.Name).ShouldBe(["7-Zip", "Caffeine", "Microsoft.VCLibs.140.00", "Slack"], ignoreOrder: true);

        all.Items.Single(t => t.Name == "Slack").Category.ShouldBe("Application");
        all.Items.Single(t => t.Name == "Caffeine").Confidence.ShouldBe("Observed");
        all.Items.Single(t => t.Name == "7-Zip").Category.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_view_is_refused()
    {
        using var client = await ClientAsync();

        var response = await client.GetAsync(new Uri("/admin/v1/software?view=everything", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
