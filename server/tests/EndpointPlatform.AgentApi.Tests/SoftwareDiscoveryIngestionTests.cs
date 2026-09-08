using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Application discovery on the wire: the identity, confidence, category,
/// package, signer and evidence an agent 1.9.0 reports are stored with the row
/// and replaced with it; a row from an older agent still lands with those
/// absent; and a value the contract does not name is refused rather than
/// stored as if it meant something.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class SoftwareDiscoveryIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const string SlackRoot = @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";
    private const string SlackFullName = "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";

    private static InventoryReport Report(IReadOnlyList<InventorySoftware>? software) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, software);

    /// <summary>Slack, as the reference machine reports it after the milestone.</summary>
    private static InventorySoftware Slack(IReadOnlyList<InventorySoftwareEvidence>? evidence = null) => new(
        "Slack", "4.52.155.0", "Slack Technologies Inc.", null, SlackRoot, null,
        "User", @"OMDEVSINH-TECHS\Techsara", null,
        IdentityKind: "Package",
        StableKey: "com.tinyspeck.slackdesktop_8yrtsj140pw4g",
        VersionKey: SlackFullName,
        Confidence: "Installed",
        Category: "Application",
        PackageFamilyName: "com.tinyspeck.slackdesktop_8yrtsj140pw4g",
        PackageFullName: SlackFullName,
        ExecutablePath: SlackRoot + @"\app\Slack.exe",
        SignerSubject: "CN=\"Slack Technologies, LLC\", O=\"Slack Technologies, LLC\"",
        SignatureStatus: "Signed",
        Evidence: evidence ??
        [
            new InventorySoftwareEvidence("PackageRegistration", "Slack", SlackFullName),
            new InventorySoftwareEvidence("StartMenuShortcut", "Slack", SlackRoot + @"\app\Slack.exe"),
            new InventorySoftwareEvidence("RunningProcess", "slack", SlackRoot + @"\app\Slack.exe"),
        ]);

    /// <summary>
    /// An authenticated device, seeded rather than enrolled: enrollment is rate
    /// limited and that budget is shared by every test in this assembly, and
    /// these tests need an authenticated device and nothing more.
    /// </summary>
    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"disc-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "discovery-ingestion-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Domain.Devices.Device.Enroll(
            org.Id, "DISC-PC", $"machine-{Guid.CreateVersion7()}", "1.9.0",
            "Microsoft Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var secret = SecretGenerator.GenerateSecret();
        db.AgentCredentials.Add(new AgentCredential(
            device.Id, SecretGenerator.GenerateKeyId(), SecretGenerator.HashSecret(secret),
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();

        var credential = await db.AgentCredentials.AsNoTracking()
            .SingleAsync(c => c.DeviceId == device.Id);

        return (device.Id, $"{credential.KeyId}.{secret}");
    }

    private static HttpRequestMessage Req(string route, object body, string? credential = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        { Content = JsonContent.Create(body) };
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    [Fact]
    public async Task Discovery_fields_and_evidence_persist_with_the_row()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report([Slack()]), credential));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceSoftware.SingleAsync(s => s.DeviceId == deviceId);
        row.IdentityKind.ShouldBe("Package");
        row.StableKey.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        row.VersionKey.ShouldBe(SlackFullName);
        row.Confidence.ShouldBe("Installed");
        row.Category.ShouldBe("Application");
        row.PackageFamilyName.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        row.PackageFullName.ShouldBe(SlackFullName);
        row.ExecutablePath.ShouldBe(SlackRoot + @"\app\Slack.exe");
        row.SignerSubject.ShouldBe("CN=\"Slack Technologies, LLC\", O=\"Slack Technologies, LLC\"");
        row.SignatureStatus.ShouldBe("Signed");
        row.InstallLocation.ShouldBe(SlackRoot, "the row Force Stop acts on is unchanged");

        var evidence = await db.DeviceSoftwareEvidence
            .Where(e => e.DeviceSoftwareId == row.Id).OrderBy(e => e.Ordinal).ToListAsync();
        evidence.Select(e => e.Source).ShouldBe(["PackageRegistration", "StartMenuShortcut", "RunningProcess"]);
        evidence.Select(e => e.Ordinal).ShouldBe([0, 1, 2]);
        evidence[0].Detail.ShouldBe(SlackFullName);
        evidence[1].Name.ShouldBe("Slack");
    }

    /// <summary>
    /// The software snapshot is replaced wholesale, and its evidence goes with
    /// it: nothing from the previous inventory survives to be attributed to the
    /// new rows.
    /// </summary>
    [Fact]
    public async Task Evidence_is_replaced_with_its_row_on_the_next_inventory()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report([Slack()]), credential));
        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report([
            Slack([new InventorySoftwareEvidence("PackageRegistration", "Slack", SlackFullName)]),
            new InventorySoftware("7-Zip", "23.01", "Igor Pavlov", null, null, "x64"),
        ]), credential));

        await using var db = _fixture.CreateDbContext();
        var rows = await db.DeviceSoftware.Where(s => s.DeviceId == deviceId).ToListAsync();
        rows.Count.ShouldBe(2);

        var ids = rows.Select(r => r.Id).ToList();
        var evidence = await db.DeviceSoftwareEvidence.Where(e => ids.Contains(e.DeviceSoftwareId)).ToListAsync();
        evidence.ShouldHaveSingleItem().Source.ShouldBe("PackageRegistration");

        // Nothing orphaned: every evidence row in the table belongs to a live software row.
        var liveIds = await db.DeviceSoftware.Select(s => s.Id).ToListAsync();
        (await db.DeviceSoftwareEvidence.CountAsync(e => !liveIds.Contains(e.DeviceSoftwareId))).ShouldBe(0);
    }

    [Fact]
    public async Task A_row_from_an_older_agent_lands_with_the_discovery_fields_absent()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(
            [new InventorySoftware("Google Chrome", "152.0", "Google LLC", null, @"C:\Program Files\Google\Chrome\Application", "x86", "Machine")]),
            credential));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceSoftware.SingleAsync(s => s.DeviceId == deviceId);
        row.IdentityKind.ShouldBeNull();
        row.Confidence.ShouldBeNull();
        row.Category.ShouldBeNull();
        row.SignerSubject.ShouldBeNull();
        (await db.DeviceSoftwareEvidence.CountAsync(e => e.DeviceSoftwareId == row.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task An_observed_executable_is_stored_as_observed_with_no_install_location()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report([
            new InventorySoftware("Caffeine", "1.98", "Zhorn Software", null, null, null, "User", @"OMDEVSINH-TECHS\Techsara", null,
                IdentityKind: "Executable", StableKey: "zhorn software\u001fcaffeine\u001fc:\\users\\techsara\\downloads",
                Confidence: "Observed", Category: "Observed",
                ExecutablePath: @"C:\Users\Techsara\Downloads\caffeine64.exe", SignatureStatus: "Unsigned",
                Evidence: [new InventorySoftwareEvidence("RunningProcess", "caffeine64", @"C:\Users\Techsara\Downloads\caffeine64.exe")]),
        ]), credential));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceSoftware.SingleAsync(s => s.DeviceId == deviceId);
        row.Confidence.ShouldBe("Observed");
        row.InstallLocation.ShouldBeNull();
        row.ExecutablePath.ShouldBe(@"C:\Users\Techsara\Downloads\caffeine64.exe");
    }

    // ---- refusals ---------------------------------------------------------------------

    /// <summary>Every shape the Agent API must refuse, with why.</summary>
    private static IEnumerable<(string Reason, InventorySoftware Row)> Malformed() =>
    [
        ("confidence the contract does not name", Slack() with { Confidence = "Referenced" }),
        ("transient is never a row", Slack() with { Confidence = "Transient" }),
        ("unknown category", Slack() with { Category = "Malware" }),
        ("unknown identity kind", Slack() with { IdentityKind = "Alien" }),
        ("signature status that claims trust", Slack() with { SignatureStatus = "Trusted" }),
        ("over-long stable key", Slack() with { StableKey = new string('k', InventorySoftware.MaxStableKey + 1) }),
        ("over-long signer", Slack() with { SignerSubject = new string('s', InventorySoftware.MaxSignerSubject + 1) }),
        ("over-long executable path", Slack() with { ExecutablePath = new string('p', InventorySoftware.MaxExecutablePath + 1) }),
        ("evidence from an unknown source", Slack([new InventorySoftwareEvidence("Telepathy", null, null)])),
        ("evidence with no source", Slack([new InventorySoftwareEvidence(" ", "x", "y")])),
        ("over-long evidence detail", Slack([new InventorySoftwareEvidence("AppPaths", null, new string('d', InventorySoftwareEvidence.MaxDetail + 1))])),
        (
            "too much evidence",
            Slack(Enumerable.Range(0, InventorySoftware.MaxEvidence + 1)
                .Select(i => new InventorySoftwareEvidence("RunningProcess", null, $@"C:\x\{i}.exe")).ToArray())
        ),
    ];

    /// <summary>
    /// One device for every refusal: a refused report stores nothing, so the
    /// cases cannot interfere, and enrolling once keeps this class well inside
    /// the Agent API's enrollment rate limit, which the classes after it share.
    /// </summary>
    [Fact]
    public async Task A_value_the_contract_does_not_name_is_refused()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        foreach (var (reason, row) in Malformed())
        {
            var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report([row]), credential));

            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest, reason);
        }

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceSoftware.CountAsync(s => s.DeviceId == deviceId)).ShouldBe(0, "a refused report stores nothing");
    }
}
