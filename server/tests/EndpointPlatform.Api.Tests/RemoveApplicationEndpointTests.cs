using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Remove: stopping and then uninstalling a named application on one or more
/// devices.
/// </summary>
/// <remarks>
/// <para>
/// The request names an application, never an identity. These tests exist
/// mostly to prove what cannot be asked for: no product code, no package name,
/// no method, nothing outside the caller's organization or scope, nothing that
/// would reach a retired device or an agent without the executor, and never
/// the agent itself.
/// </para>
/// <para>
/// The server queues exactly one <see cref="DeviceTaskType.RemoveApplication"/>
/// per device carrying the identity inventory recorded, and reports every row it
/// will not act on with the reason, so an operator is told rather than left to
/// retry something that cannot work.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class RemoveApplicationEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Remove = new("/admin/v1/software/remove", UriKind.Relative);

    private const string ChromeDir = @"C:\Program Files\Google\Chrome\Application";
    private const string ChromeCode = "{8A69D345-D564-463C-AFF1-A69D9E530F96}";
    private const string ChromeUpgrade = "{1D3EC7B4-7F3A-4E2C-9C1E-2B5A6F8D9E01}";
    private const string SlackRoot = @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";
    private const string SlackFullName = "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";
    private const string SlackFamily = "com.tinyspeck.slackdesktop_8yrtsj140pw4g";

    /// <summary>The first agent version carrying the RemoveApplication executor.</summary>
    private const string SupportedAgent = "1.10.0";

    private sealed record DeviceOutcome(
        Guid DeviceId, string Hostname, string Outcome, string? Reason, string? Method, Guid? TaskId);

    private sealed record RemoveResponse(int DevicesQueued, IReadOnlyList<DeviceOutcome> Devices);

    private async Task<HttpClient> AdminAsync(string email = AdminApiPostgresFixture.SuperAdminEmail)
    {
        var token = await _fixture.SignInAsync(email);
        return _fixture.CreateClientFor(token);
    }

    // ------------------------------------------------------------------ rows

    private static DeviceSoftware ChromeMsi(
        Guid deviceId, DateTimeOffset now, string scope = "Machine", string? location = ChromeDir,
        string version = "152.0.1", string name = "Google Chrome", string? upgradeCode = ChromeUpgrade,
        string? identityKind = "WindowsInstaller", string? publisher = "Google LLC",
        string productCode = ChromeCode) =>
        new(deviceId, name, version, publisher, null, location, "x86", now,
            scope, scope == "User" ? @"CORP\alice" : null, productCode, identityKind, null, null,
            "Installed", "Application", null, null, upgradeCode);

    private static DeviceSoftware SlackPackage(
        Guid deviceId, DateTimeOffset now, string category = "Application",
        string family = SlackFamily, string fullName = SlackFullName) =>
        new(deviceId, "Slack", "4.52.155.0", "Slack Technologies", null, SlackRoot, null, now,
            "User", @"CORP\alice", null, "Package", family, fullName, "Installed", category,
            family, fullName, null, SlackRoot + @"\app\Slack.exe", "CN=Slack Technologies, LLC", "Signed");

    /// <summary>A traditional EXE installer's registration: no typed identity.</summary>
    private static DeviceSoftware RegisteredExe(Guid deviceId, DateTimeOffset now) =>
        new(deviceId, "Google Chrome", "152.0.1", "Google LLC", null, ChromeDir, "x86", now,
            "Machine", null, null, "Registered", "google llc\u001fgoogle chrome\u001f" + ChromeDir.ToLowerInvariant(),
            null, "Installed", "Application");

    /// <summary>What an agent older than 1.9.0 reports: no identity at all.</summary>
    private static DeviceSoftware LegacyRow(Guid deviceId, DateTimeOffset now) =>
        new(deviceId, "Google Chrome", "152.0.1", "Google LLC", null, ChromeDir, "x86", now,
            "Machine", null, ChromeCode);

    private async Task<Guid> SeedAsync(
        string hostname,
        Func<Guid, DateTimeOffset, DeviceSoftware[]> rows,
        DeviceStatus status = DeviceStatus.Active,
        string agentVersion = SupportedAgent,
        Guid? organizationId = null)
    {
        await using var db = _fixture.CreateDbContext();
        organizationId ??= await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId.Value, $"rm-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "remove-tests", now.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId.Value, hostname, $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Microsoft Windows 11 Pro", token.Id, now);

        if (status == DeviceStatus.Retired)
        {
            device.Retire();
        }

        db.Devices.Add(device);
        db.DeviceSoftware.AddRange(rows(device.Id, now));

        // A stale process snapshot. Nothing here may influence the queued task.
        db.DeviceProcesses.Add(new DeviceProcessEntry(
            device.Id, 4321, "chrome", 100_000, $@"{ChromeDir}\chrome.exe", now));

        await db.SaveChangesAsync();
        return device.Id;
    }

    private static async Task<List<DeviceTask>> RemoveTasksAsync(AdminApiPostgresFixture f, Guid deviceId)
    {
        await using var db = f.CreateDbContext();
        return await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.RemoveApplication)
            .ToListAsync();
    }

    private static object Body(
        IEnumerable<Guid> deviceIds, string name = "Google Chrome", string? publisher = null, string? version = null) =>
        new { deviceIds, name, publisher, version };

    /// <summary>
    /// The payload as the agent reads it. Parsed rather than matched as text:
    /// the column is jsonb, and PostgreSQL reformats what it stores.
    /// </summary>
    private static JsonElement Payload(DeviceTask task)
    {
        task.PayloadJson.ShouldNotBeNull();
        return JsonDocument.Parse(task.PayloadJson!).RootElement.Clone();
    }

    private async Task<RemoveResponse> PostAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Remove, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<RemoveResponse>())!;
    }

    // ------------------------------------------------------------------- core

    /// <summary>
    /// One task per device, carrying the product code inventory recorded, the
    /// method the endpoint must use, and the install directory for the stop step.
    /// </summary>
    [Fact]
    public async Task Remove_queues_one_task_carrying_the_product_code_and_the_method()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-MSI", (id, now) => [ChromeMsi(id, now)]);

        var response = await client.PostAsJsonAsync(Remove, Body([device]));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location.ShouldNotBeNull().ToString().ShouldBe("/admin/v1/software");

        var result = (await response.Content.ReadFromJsonAsync<RemoveResponse>())!;
        result.DevicesQueued.ShouldBe(1);

        var outcome = result.Devices.Single();
        outcome.Hostname.ShouldBe("RM-MSI");
        outcome.Outcome.ShouldBe("Queued");
        outcome.Method.ShouldBe("WindowsInstaller");
        outcome.Reason.ShouldBeNull();

        var task = (await RemoveTasksAsync(_fixture, device)).Single();
        outcome.TaskId.ShouldBe(task.Id);

        var payload = Payload(task);
        payload.GetProperty("applicationName").GetString().ShouldBe("Google Chrome");
        payload.GetProperty("method").GetString().ShouldBe("WindowsInstaller");
        payload.GetProperty("productCode").GetString().ShouldBe(ChromeCode);
        payload.GetProperty("installLocation").GetString().ShouldBe(ChromeDir);
        payload.GetProperty("packageFullName").ValueKind.ShouldBe(JsonValueKind.Null);

        // No pid from the stale snapshot reaches the endpoint.
        task.PayloadJson!.Contains("4321", StringComparison.Ordinal).ShouldBeFalse();
        task.PayloadJson.Contains("processId", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    /// <summary>
    /// The uninstall needs the identity, not the directory. Without a usable
    /// directory the stop step is skipped on the endpoint, and the task carries
    /// an explicit null so it knows to.
    /// </summary>
    [Fact]
    public async Task An_msi_without_a_usable_install_location_still_queues_the_uninstall()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-NOPATH", (id, now) => [ChromeMsi(id, now, location: @"C:\Program Files")]);

        var result = await PostAsync(client, Body([device]));

        result.Devices.Single().Outcome.ShouldBe("Queued");
        var task = (await RemoveTasksAsync(_fixture, device)).Single();
        Payload(task).GetProperty("installLocation").ValueKind.ShouldBe(JsonValueKind.Null);
        task.PayloadJson!.Contains("Program Files", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_package_is_removed_by_full_name()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-PKG", (id, now) => [SlackPackage(id, now)]);

        var result = await PostAsync(client, Body([device], name: "Slack"));

        var outcome = result.Devices.Single();
        outcome.Outcome.ShouldBe("Queued");
        outcome.Method.ShouldBe("Package");

        var payload = Payload((await RemoveTasksAsync(_fixture, device)).Single());
        payload.GetProperty("method").GetString().ShouldBe("Package");
        payload.GetProperty("packageFullName").GetString().ShouldBe(SlackFullName);
        payload.GetProperty("productCode").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A device can hold the same product machine-wide and for one account. The
    /// row that can be acted on is the one acted on.
    /// </summary>
    [Fact]
    public async Task When_a_machine_and_a_per_user_row_both_match_the_machine_one_is_removed()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-BOTH", (id, now) =>
            [ChromeMsi(id, now, scope: "User"), ChromeMsi(id, now, scope: "Machine")]);

        var result = await PostAsync(client, Body([device]));

        result.Devices.Single().Outcome.ShouldBe("Queued");
        result.Devices.Single().Method.ShouldBe("WindowsInstaller");
        (await RemoveTasksAsync(_fixture, device)).Count.ShouldBe(1);
    }

    /// <summary>
    /// Several removable rows of one application on one device -- what a failed
    /// upgrade leaves behind -- and which one is uninstalled is a defined choice,
    /// not whichever row the database happened to return first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The versions are picked so that a wrong answer is the answer under either
    /// plan PostgreSQL might choose for this query: 1.0.0 is seeded first, so it
    /// leads a heap scan, and it also sorts first in
    /// <c>ix_device_software_name_version</c>, so it leads an index scan too.
    /// Neither is the row that should be removed.
    /// </para>
    /// <para>
    /// 9.0.1 against 10.0.1 then pins the comparison as numeric: ordered as text,
    /// "9.0.1" ranks above "10.0.1" and the older build would be uninstalled.
    /// </para>
    /// <para>
    /// Repeated, because the defect being guarded against is not "picks the wrong
    /// row" but "picks a row that is not always the same one".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Several_removable_rows_of_one_application_queue_the_same_row_every_time()
    {
        const string ancientCode = "{11111111-1111-4111-8111-111111111111}";
        const string olderCode = "{22222222-2222-4222-8222-222222222222}";
        const string newestCode = "{33333333-3333-4333-8333-333333333333}";

        using var client = await AdminAsync();
        var device = await SeedAsync("RM-MANY-ROWS", (id, now) =>
        [
            ChromeMsi(id, now, version: "1.0.0", productCode: ancientCode),
            ChromeMsi(id, now, version: "9.0.1", productCode: olderCode),
            ChromeMsi(id, now, version: "10.0.1", productCode: newestCode),
        ]);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await PostAsync(client, Body([device]))).Devices.Single().Outcome.ShouldBe("Queued");
        }

        var queuedCodes = (await RemoveTasksAsync(_fixture, device))
            .Select(t => Payload(t).GetProperty("productCode").GetString())
            .ToList();

        queuedCodes.Count.ShouldBe(3);
        queuedCodes.ShouldAllBe(code => code == newestCode);
    }

    /// <summary>
    /// Machine scope leads the ordering even when the per-user row is removable
    /// in its own right: it is the more specific match, and the only one an
    /// endpoint acting as SYSTEM can uninstall for every account.
    /// </summary>
    /// <remarks>
    /// Both rows here are removable -- unlike the machine/per-user MSI pair,
    /// where the per-user one is refused outright -- so it is the ordering, not
    /// the removability check, that decides. The per-user package is seeded
    /// first and its version sorts first, so it leads whichever way the row set
    /// is read.
    /// </remarks>
    [Fact]
    public async Task A_machine_wide_row_wins_over_a_per_user_row_that_is_also_removable()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-SCOPE-ORDER", (id, now) =>
        [
            SlackPackage(id, now),
            ChromeMsi(id, now, name: "Slack", scope: "Machine", version: "5.0.0"),
        ]);

        var result = await PostAsync(client, Body([device], name: "Slack"));

        result.Devices.Single().Method.ShouldBe("WindowsInstaller");

        var payload = Payload((await RemoveTasksAsync(_fixture, device)).Single());
        payload.GetProperty("productCode").GetString().ShouldBe(ChromeCode);
        payload.GetProperty("packageFullName").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Several_devices_are_handled_in_one_request()
    {
        using var client = await AdminAsync();
        var a = await SeedAsync("RM-MULTI-A", (id, now) => [ChromeMsi(id, now)]);
        var b = await SeedAsync("RM-MULTI-B", (id, now) => [RegisteredExe(id, now)]);

        var result = await PostAsync(client, Body([a, b]));

        result.Devices.Count.ShouldBe(2);
        result.Devices.Single(d => d.DeviceId == a).Outcome.ShouldBe("Queued");
        result.Devices.Single(d => d.DeviceId == b).Outcome.ShouldBe("NotRemovable");
        result.DevicesQueued.ShouldBe(1);
    }

    // ---------------------------------------------------------- not removable

    /// <summary>
    /// msi.dll runs as SYSTEM on the endpoint and cannot see another account's
    /// per-user product. Reported, not attempted.
    /// </summary>
    [Fact]
    public async Task A_per_user_msi_is_reported_not_removable()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-PERUSER", (id, now) => [ChromeMsi(id, now, scope: "User")]);

        var result = await PostAsync(client, Body([device]));

        var outcome = result.Devices.Single();
        outcome.Outcome.ShouldBe("NotRemovable");
        outcome.Reason.ShouldBe("PerUserInstall");
        outcome.Method.ShouldBeNull();
        outcome.TaskId.ShouldBeNull();
        result.DevicesQueued.ShouldBe(0);
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_windows_inbox_package_is_a_protected_system_component()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-INBOX", (id, now) => [SlackPackage(id, now,
            family: "windows.immersivecontrolpanel_cw5n1h2txyewy",
            fullName: "windows.immersivecontrolpanel_10.0.6.1000_neutral_neutral_cw5n1h2txyewy")]);

        var result = await PostAsync(client, Body([device], name: "Slack"));

        result.Devices.Single().Outcome.ShouldBe("NotRemovable");
        result.Devices.Single().Reason.ShouldBe("SystemComponent");
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("InboxApp")]
    [InlineData("FrameworkOrResource")]
    [InlineData("Component")]
    public async Task A_package_in_a_system_category_is_a_protected_system_component(string category)
    {
        using var client = await AdminAsync();
        var device = await SeedAsync($"RM-SYS-{Guid.CreateVersion7():N}"[..12],
            (id, now) => [SlackPackage(id, now, category: category)]);

        var result = await PostAsync(client, Body([device], name: "Slack"));

        result.Devices.Single().Outcome.ShouldBe("NotRemovable");
        result.Devices.Single().Reason.ShouldBe("SystemComponent");
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// The agent is an installed application and is refused as one: by name, and
    /// by the upgrade code that survives every release and any renamed product.
    /// </summary>
    [Fact]
    public async Task The_agent_cannot_be_asked_to_remove_itself()
    {
        using var client = await AdminAsync();
        var byName = await SeedAsync("RM-AGENT-NAME",
            (id, now) => [ChromeMsi(id, now, name: "Endpoint Platform Agent", upgradeCode: null)]);
        var byCode = await SeedAsync("RM-AGENT-CODE",
            (id, now) => [ChromeMsi(id, now, name: "Renamed Product", upgradeCode: "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}")]);

        var named = await PostAsync(client, Body([byName], name: "Endpoint Platform Agent"));
        named.Devices.Single().Outcome.ShouldBe("NotRemovable");
        named.Devices.Single().Reason.ShouldBe("ProtectedAgent");

        var coded = await PostAsync(client, Body([byCode], name: "Renamed Product"));
        coded.Devices.Single().Outcome.ShouldBe("NotRemovable");
        coded.Devices.Single().Reason.ShouldBe("ProtectedAgent");

        (await RemoveTasksAsync(_fixture, byName)).ShouldBeEmpty();
        (await RemoveTasksAsync(_fixture, byCode)).ShouldBeEmpty();
    }

    /// <summary>
    /// An EXE-installer registration has an uninstaller the agent does not launch
    /// (ADR-0005); a row an older agent reported has no identity at all. Both are
    /// refused with the same reason, because both lack the same thing.
    /// </summary>
    [Fact]
    public async Task A_row_without_a_typed_installer_identity_is_reported_not_removable()
    {
        using var client = await AdminAsync();
        var registered = await SeedAsync("RM-EXE", (id, now) => [RegisteredExe(id, now)]);
        var legacy = await SeedAsync("RM-LEGACY", (id, now) => [LegacyRow(id, now)]);

        var result = await PostAsync(client, Body([registered, legacy]));

        result.Devices.Count.ShouldBe(2);
        result.Devices.ShouldAllBe(d => d.Outcome == "NotRemovable" && d.Reason == "NoInstallerIdentity");
        result.DevicesQueued.ShouldBe(0);
        (await RemoveTasksAsync(_fixture, registered)).ShouldBeEmpty();
        (await RemoveTasksAsync(_fixture, legacy)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_device_without_the_application_is_reported_as_not_installed()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-OTHER", (id, now) => [SlackPackage(id, now)]);

        var result = await PostAsync(client, Body([device]));

        result.Devices.Single().Outcome.ShouldBe("NotInstalled");
        result.Devices.Single().Reason.ShouldBeNull();
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// The operator clicked a specific row. A pinned version matches exactly that
    /// version and nothing else, so a different build is never removed by proxy.
    /// </summary>
    [Fact]
    public async Task A_version_pin_matches_only_that_exact_version()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-VERSION", (id, now) => [ChromeMsi(id, now, version: "152.0.1")]);

        var other = await PostAsync(client, Body([device], version: "151.0.0"));
        other.Devices.Single().Outcome.ShouldBe("NotInstalled");
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();

        var exact = await PostAsync(client, Body([device], version: "152.0.1"));
        exact.Devices.Single().Outcome.ShouldBe("Queued");
        (await RemoveTasksAsync(_fixture, device)).Count.ShouldBe(1);
    }

    // ------------------------------------------------------- agent capability

    /// <summary>
    /// Refused before a row exists rather than delivered and reported as an
    /// unknown task type, which would read exactly like the uninstall failing.
    /// </summary>
    [Fact]
    public async Task An_agent_without_the_executor_is_reported_ineligible_not_failed()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-OLDAGENT", (id, now) => [ChromeMsi(id, now)], agentVersion: "1.9.0");

        var result = await PostAsync(client, Body([device]));

        result.DevicesQueued.ShouldBe(0);
        result.Devices.Single().Outcome.ShouldBe("NotEligible");
        result.Devices.Single().Reason.ShouldBeNull();
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    // --------------------------------------------------------------- security

    /// <summary>Retired devices receive no tasks of any kind.</summary>
    [Fact]
    public async Task A_retired_device_receives_no_task()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-RETIRED", (id, now) => [ChromeMsi(id, now)], status: DeviceStatus.Retired);

        var result = await PostAsync(client, Body([device]));

        result.DevicesQueued.ShouldBe(0);
        result.Devices.Single().Outcome.ShouldBe("NotEligible");
        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// A device in another organization resolves to nothing rather than being
    /// refused, so the response cannot be used to discover that it exists.
    /// </summary>
    [Fact]
    public async Task A_device_in_another_organization_is_dropped()
    {
        Guid foreignOrg;
        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("Other Org", ("o" + Guid.CreateVersion7().ToString("N"))[..20]);
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            foreignOrg = org.Id;
        }

        var foreign = await SeedAsync("RM-FOREIGN", (id, now) => [ChromeMsi(id, now)], organizationId: foreignOrg);
        var unknown = Guid.CreateVersion7();

        using var client = await AdminAsync();
        var result = await PostAsync(client, Body([foreign, unknown]));

        result.Devices.ShouldBeEmpty();
        result.DevicesQueued.ShouldBe(0);
        (await RemoveTasksAsync(_fixture, foreign)).ShouldBeEmpty();
    }

    /// <summary>
    /// An administrator restricted to a group acts on that group's devices only.
    /// The other device is narrowed away, not refused, so scope never reveals it.
    /// </summary>
    [Fact]
    public async Task A_device_outside_the_administrators_scope_is_dropped()
    {
        var inScope = await SeedAsync("RM-SCOPE-IN", (id, now) => [ChromeMsi(id, now)]);
        var outOfScope = await SeedAsync("RM-SCOPE-OUT", (id, now) => [ChromeMsi(id, now)]);

        using var client = await ScopedAdminAsync(inScope);
        var result = await PostAsync(client, Body([inScope, outOfScope]));

        result.Devices.Select(d => d.DeviceId).ShouldBe([inScope]);
        result.Devices.Single().Outcome.ShouldBe("Queued");
        (await RemoveTasksAsync(_fixture, outOfScope)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_without_an_application_or_a_device_is_refused()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-VALIDATION", (id, now) => [ChromeMsi(id, now)]);

        (await client.PostAsJsonAsync(Remove, new { deviceIds = new[] { device }, name = "" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync(Remove, new { deviceIds = Array.Empty<Guid>(), name = "Google Chrome" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync(Remove, new { deviceIds = new[] { device }, name = new string('x', 385) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var tooMany = Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7()).ToArray();
        (await client.PostAsJsonAsync(Remove, new { deviceIds = tooMany, name = "Google Chrome" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// A publisher or version longer than inventory can hold is refused at the
    /// edge.
    /// </summary>
    /// <remarks>
    /// Both values only narrow which inventory row is chosen, and inventory
    /// bounds them itself -- a publisher to 256 characters, a version to 128 --
    /// so a longer one cannot match anything and is refused rather than carried
    /// into a query. The same bound is what keeps an arbitrary caller string out
    /// of a task payload on the Force Stop path, which does persist it.
    /// </remarks>
    [Fact]
    public async Task A_publisher_or_version_longer_than_inventory_can_hold_is_refused()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-BOUNDS", (id, now) => [ChromeMsi(id, now)]);

        (await client.PostAsJsonAsync(Remove, Body([device], publisher: new string('p', 257))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync(Remove, Body([device], version: new string('9', 129))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The bounds are inventory's own, not arbitrary ones: a value at the
        // limit is a legitimate request, and fails only by matching no row.
        (await client.PostAsJsonAsync(Remove, Body([device], publisher: new string('p', 256))))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// The task carries the publisher <em>inventory</em> recorded, never the one
    /// the caller sent.
    /// </summary>
    /// <remarks>
    /// A request's publisher narrows which row is matched and goes no further. A
    /// row inventory recorded no publisher for still matches a request that names
    /// one -- inventory omits publishers often, and treating that as a mismatch
    /// would make an installed application unremovable -- so that is exactly the
    /// path on which a caller's string could otherwise reach
    /// <c>device_tasks.payload_json</c> and the <c>task.queue</c> audit entry
    /// copied from it.
    /// </remarks>
    [Fact]
    public async Task A_queued_payload_never_carries_a_publisher_inventory_did_not_record()
    {
        using var client = await AdminAsync();
        var recorded = await SeedAsync("RM-PUB-KNOWN", (id, now) => [ChromeMsi(id, now)]);
        var silent = await SeedAsync("RM-PUB-NONE", (id, now) => [ChromeMsi(id, now, publisher: null)]);

        // The longest publisher the endpoint accepts: inside the bound, and still
        // nothing any inventory row ever reported.
        var claimed = new string('p', 256);

        (await PostAsync(client, Body([silent], publisher: claimed)))
            .Devices.Single().Outcome.ShouldBe("Queued");

        var task = (await RemoveTasksAsync(_fixture, silent)).Single();
        Payload(task).GetProperty("publisher").ValueKind.ShouldBe(JsonValueKind.Null);
        task.PayloadJson!.Contains(claimed, StringComparison.Ordinal).ShouldBeFalse();

        // And where inventory did record one, that is what the endpoint is told.
        await PostAsync(client, Body([recorded], publisher: "Google LLC"));
        Payload((await RemoveTasksAsync(_fixture, recorded)).Single())
            .GetProperty("publisher").GetString().ShouldBe("Google LLC");
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_remove_anything()
    {
        using var anonymous = _fixture.Factory.CreateClient();

        (await anonymous.PostAsJsonAsync(Remove, Body([Guid.CreateVersion7()])))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Removal is gated on software.deploy. Auditor can see the software; Helpdesk
    /// can see it too and can even queue some tasks; neither may change what a
    /// machine runs.
    /// </summary>
    [Theory]
    [InlineData(AdminApiPostgresFixture.AuditorEmail)]
    [InlineData(AdminApiPostgresFixture.HelpdeskEmail)]
    public async Task A_role_without_software_deploy_cannot_remove(string email)
    {
        var device = await SeedAsync($"RM-ROLE-{Guid.CreateVersion7():N}"[..12], (id, now) => [ChromeMsi(id, now)]);

        using var client = await AdminAsync(email);
        (await client.PostAsJsonAsync(Remove, Body([device])))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await RemoveTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// The contract accepts an application name, a publisher and a version, and
    /// nothing else. A caller cannot smuggle in a product code, a package name or
    /// a method: the identity comes from inventory, so no request can ask the
    /// fleet to uninstall something arbitrary.
    /// </summary>
    [Fact]
    public async Task A_product_code_package_name_or_method_supplied_by_the_client_is_ignored()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("RM-INJECT", (id, now) => [ChromeMsi(id, now)]);

        var response = await client.PostAsJsonAsync(Remove, new
        {
            deviceIds = new[] { device },
            name = "Google Chrome",
            // None of these are part of the contract.
            productCode = "{DEADBEEF-0000-4000-8000-000000000000}",
            packageFullName = "Microsoft.Windows.ShellExperienceHost_10.0.1_neutral_neutral_cw5n1h2txyewy",
            method = "Package",
            installLocation = @"C:\Windows",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var task = (await RemoveTasksAsync(_fixture, device)).Single();
        var payload = Payload(task);

        // Everything the endpoint acts on came from inventory, not the request.
        payload.GetProperty("method").GetString().ShouldBe("WindowsInstaller");
        payload.GetProperty("productCode").GetString().ShouldBe(ChromeCode);
        payload.GetProperty("packageFullName").ValueKind.ShouldBe(JsonValueKind.Null);
        payload.GetProperty("installLocation").GetString().ShouldBe(ChromeDir);

        task.PayloadJson!.Contains("DEADBEEF", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        task.PayloadJson.Contains("ShellExperienceHost", StringComparison.Ordinal).ShouldBeFalse();
        task.PayloadJson.Contains(@"C:\\Windows", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task A_removal_is_audited_with_hostnames_and_outcomes_but_no_paths()
    {
        using var client = await AdminAsync();
        var queued = await SeedAsync("RM-AUDIT-A", (id, now) => [ChromeMsi(id, now)]);
        var refused = await SeedAsync("RM-AUDIT-B", (id, now) => [ChromeMsi(id, now, scope: "User")]);

        await PostAsync(client, Body([queued, refused]));

        await using var db = _fixture.CreateDbContext();
        var entry = await db.AuditLogEntries
            .Where(a => a.Action == "software.application.remove")
            .OrderByDescending(a => a.OccurredAt)
            .FirstAsync();

        entry.TargetType.ShouldBe("application");
        entry.TargetDisplay.ShouldBe("Google Chrome");
        entry.RequiredPermission.ShouldBe(Permissions.Software.Deploy);
        entry.NewState.ShouldNotBeNull();

        // Parsed, not matched as text: jsonb reformats what it stores.
        using var state = JsonDocument.Parse(entry.NewState!);
        state.RootElement.GetProperty("application").GetString().ShouldBe("Google Chrome");
        state.RootElement.GetProperty("devices").GetInt32().ShouldBe(2);
        state.RootElement.GetProperty("devicesQueued").GetInt32().ShouldBe(1);

        var results = state.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => (
                Hostname: r.GetProperty("hostname").GetString(),
                Outcome: r.GetProperty("outcome").GetString(),
                Reason: r.GetProperty("reason").GetString()))
            .ToList();
        results.ShouldContain(("RM-AUDIT-A", "Queued", null));
        results.ShouldContain(("RM-AUDIT-B", "NotRemovable", "PerUserInstall"));

        // Outcomes, reasons and hostnames only -- not where the binary lives on
        // disk, and not the product identity either; the task record has that.
        entry.NewState!.Contains(@"C:\", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        entry.NewState.Contains(ChromeCode, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A Super Administrator whose device scope is one static group holding only
    /// <paramref name="deviceId"/>: full permissions, narrow reach.
    /// </summary>
    private async Task<HttpClient> ScopedAdminAsync(Guid deviceId)
    {
        var email = $"rm-scoped-{Guid.CreateVersion7():N}@test.local";
        var token = SecretGenerator.GenerateSecret();

        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.SuperAdministrator);

        var user = new PlatformUser(org.Id, email, "Scoped Admin");
        user.SetPasswordHash(PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
        user.AssignRole(role.Id);
        db.PlatformUsers.Add(user);

        var group = new DeviceGroup(org.Id, $"Scope {Guid.CreateVersion7():N}"[..20], "remove tests", DeviceGroupType.Static);
        db.DeviceGroups.Add(group);
        db.DeviceGroupMemberships.Add(new DeviceGroupMembership(group.Id, deviceId));
        db.AdminDeviceScopes.Add(new AdminDeviceScope(user.Id, group.Id));

        db.AdminSessions.Add(new AdminSession(
            user.Id, SecretGenerator.HashSecret(token), user.SecurityStamp,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            sourceIp: null, userAgent: "remove-tests"));

        await db.SaveChangesAsync();
        return _fixture.CreateClientFor(token);
    }
}
