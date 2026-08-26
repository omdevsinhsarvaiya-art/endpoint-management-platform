using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The agent's USB channel over real HTTP + PostgreSQL.
/// </summary>
/// <remarks>
/// The endpoint is the untrusted party in this exchange. It reports what
/// hardware it can see and confesses what it is enforcing; the server records
/// both and answers with a policy computed entirely from decisions
/// administrators have already made. The tests here are mostly about that
/// asymmetry: nothing an agent says about itself can widen what it is allowed to
/// do.
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class UsbReportTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const string StickId = @"USB\VID_0781&PID_5581\USBTEST1";

    private async Task<(Guid DeviceId, string Credential, Guid OrgId)> EnrollAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();

        db.EnrollmentTokens.Add(new EnrollmentToken(
            org.Id, $"tk-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Request(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, hostname, $"machine-{Guid.CreateVersion7():N}", "1.1.2", null)));

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}", org.Id);
    }

    private static HttpRequestMessage Request(
        string route, object? body = null, string? credential = null, int? protocolVersion = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative));

        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        message.Headers.Add(
            AgentProtocol.Headers.ProtocolVersion,
            (protocolVersion ?? AgentProtocol.Version).ToString());

        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    private static UsbDeviceReport Storage(
        string instanceId = StickId, string? enforced = "Restricted", string? error = null) =>
        new(instanceId, "Storage", "0781", "5581", "ABC123", "SanDisk", "Cruzer Fit",
            @"USB\VID_0781&PID_5581", IsConnected: true, enforced, error);

    private async Task<UsbPolicyResponse> ReportAsync(string credential, params UsbDeviceReport[] devices)
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Request(
            AgentProtocol.Routes.Usb,
            new UsbReport(devices, DateTimeOffset.UtcNow),
            credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UsbPolicyResponse>())!;
    }

    // ---- what a report can and cannot do -----------------------------------

    [Fact]
    public async Task A_first_report_records_the_device_and_returns_an_empty_policy()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-1");

        var policy = await ReportAsync(credential, Storage());

        // Nothing has been granted, so nothing is published. Restricted is the
        // state the endpoint is already in and the state it stays in.
        policy.Grants.ShouldBeEmpty();

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking()
            .SingleAsync(u => u.DeviceId == deviceId && u.InstanceId == StickId);

        usb.DeviceClass.ShouldBe(UsbDeviceClass.Storage);
        usb.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        usb.VendorId.ShouldBe("0781");
        usb.SerialNumber.ShouldBe("ABC123");
        usb.IsConnected.ShouldBeTrue();
    }

    /// <summary>
    /// An agent claiming to enforce read-only does not thereby get read-only.
    /// </summary>
    [Fact]
    public async Task An_agent_cannot_grant_itself_access_by_reporting_it()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-2");

        var policy = await ReportAsync(credential, Storage(enforced: "ReadOnly"));

        policy.Grants.ShouldBeEmpty();

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        // The decision stays Restricted; the claim is recorded separately so the
        // console can show that the endpoint and the platform disagree.
        usb.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        usb.EnforcedPolicy.ShouldBe(UsbStoragePolicy.ReadOnly);
        usb.IsPolicyEnforced.ShouldBeFalse();
    }

    /// <summary>
    /// An administrator's grant reaches the endpoint through the report response.
    /// </summary>
    /// <remarks>
    /// This is the convergence path that makes a lost task harmless: no
    /// <c>ApplyUsbPolicy</c> is delivered anywhere in this test, and the endpoint
    /// still learns about the grant the next time it reports.
    /// </remarks>
    [Fact]
    public async Task A_live_grant_is_published_to_the_endpoint_on_its_next_report()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("USB-RPT-3");
        await ReportAsync(credential, Storage());

        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);

        await using (var db = _fixture.CreateDbContext())
        {
            var usb = await db.UsbDevices.SingleAsync(u => u.DeviceId == deviceId);

            db.UsbAccessRequests.Add(UsbAccessRequest.GrantByAdministrator(
                orgId, deviceId, usb.Id, usb.InstanceId, UsbStoragePolicy.ReadOnly, "Vendor firmware.",
                Guid.CreateVersion7(), "admin@test", TimeSpan.FromHours(2), DateTimeOffset.UtcNow));

            await db.SaveChangesAsync();
        }

        var policy = await ReportAsync(credential, Storage());

        policy.Grants.Count.ShouldBe(1);
        policy.Grants[0].InstanceId.ShouldBe(StickId);
        policy.Grants[0].Policy.ShouldBe("ReadOnly");
        policy.Grants[0].ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>
    /// A grant whose deadline has passed is not published, sweep or no sweep.
    /// </summary>
    /// <remarks>
    /// The request row here is left in the Approved state deliberately — the
    /// expiry sweeper has not run. Publication is computed from the clock, so
    /// the endpoint is told nothing regardless of the stored status. If this
    /// ever failed, an expired grant would keep being handed out to any endpoint
    /// that reported before the sweeper caught up.
    /// </remarks>
    [Fact]
    public async Task An_expired_grant_is_never_published_even_before_the_sweeper_runs()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("USB-RPT-4");
        await ReportAsync(credential, Storage());

        await using (var db = _fixture.CreateDbContext())
        {
            var usb = await db.UsbDevices.SingleAsync(u => u.DeviceId == deviceId);

            // Granted three hours ago for one hour: lapsed two hours ago.
            var grantedAt = DateTimeOffset.UtcNow.AddHours(-3);
            db.UsbAccessRequests.Add(UsbAccessRequest.GrantByAdministrator(
                orgId, deviceId, usb.Id, usb.InstanceId, UsbStoragePolicy.ReadOnly, "Expired grant.",
                Guid.CreateVersion7(), "admin@test", TimeSpan.FromHours(1), grantedAt));

            await db.SaveChangesAsync();
        }

        var policy = await ReportAsync(credential, Storage());

        policy.Grants.ShouldBeEmpty();

        await using var check = _fixture.CreateDbContext();
        var request = await check.UsbAccessRequests.AsNoTracking()
            .SingleAsync(r => r.DeviceId == deviceId);

        // Still Approved on paper — and still not published.
        request.Status.ShouldBe(UsbAccessRequestStatus.Approved);
    }

    [Fact]
    public async Task A_grant_is_scoped_to_one_endpoint_and_not_published_to_another()
    {
        var (deviceA, credentialA, orgId) = await EnrollAsync("USB-RPT-5A");
        var (_, credentialB, _) = await EnrollAsync("USB-RPT-5B");

        await ReportAsync(credentialA, Storage());
        await ReportAsync(credentialB, Storage());

        await using (var db = _fixture.CreateDbContext())
        {
            var usb = await db.UsbDevices.SingleAsync(u => u.DeviceId == deviceA);

            db.UsbAccessRequests.Add(UsbAccessRequest.GrantByAdministrator(
                orgId, deviceA, usb.Id, usb.InstanceId, UsbStoragePolicy.ReadOnly, "Only for A.",
                Guid.CreateVersion7(), "admin@test", TimeSpan.FromHours(2), DateTimeOffset.UtcNow));

            await db.SaveChangesAsync();
        }

        (await ReportAsync(credentialA, Storage())).Grants.Count.ShouldBe(1);

        // Same hardware, same instance id, different machine: no grant.
        (await ReportAsync(credentialB, Storage())).Grants.ShouldBeEmpty();
    }

    // ---- reporting mechanics -----------------------------------------------

    [Fact]
    public async Task Devices_absent_from_a_later_report_are_marked_disconnected()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-6");
        const string keyboard = @"USB\VID_046D&PID_C31C\5&9&0&1";

        await ReportAsync(
            credential,
            Storage(),
            new UsbDeviceReport(keyboard, "Keyboard", "046D", "C31C", null, "Logitech", "K120", null, true, null, null));

        await ReportAsync(
            credential,
            new UsbDeviceReport(keyboard, "Keyboard", "046D", "C31C", null, "Logitech", "K120", null, true, null, null));

        await using var db = _fixture.CreateDbContext();
        var devices = await db.UsbDevices.AsNoTracking().Where(u => u.DeviceId == deviceId).ToListAsync();

        devices.Count.ShouldBe(2);
        devices.Single(u => u.InstanceId == StickId).IsConnected.ShouldBeFalse();
        devices.Single(u => u.InstanceId == keyboard).IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task An_enforcement_failure_is_recorded_rather_than_discarded()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-7");

        await ReportAsync(credential, Storage(enforced: null, error: "SetupDiCallClassInstaller failed (Win32 5)."));

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.EnforcementError.ShouldBe("SetupDiCallClassInstaller failed (Win32 5).");
        usb.EnforcedPolicy.ShouldBeNull();
        usb.IsPolicyEnforced.ShouldBeFalse();
    }

    /// <summary>
    /// A device class the server does not model is stored, not guessed at.
    /// </summary>
    /// <remarks>
    /// Unknown is the safe landing place: only Storage can be granted access, so
    /// an unrecognised class degrades to "visible but not grantable" rather than
    /// being rounded to whatever member sits nearby.
    /// </remarks>
    [Fact]
    public async Task An_unrecognised_device_class_is_stored_as_unknown()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-8");

        await ReportAsync(credential, new UsbDeviceReport(
            @"USB\VID_1234&PID_5678\WEIRD", "TeleportationPad", "1234", "5678",
            null, null, null, null, true, null, null));

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.DeviceClass.ShouldBe(UsbDeviceClass.Unknown);
        usb.IsStorage.ShouldBeFalse();
    }

    [Fact]
    public async Task Re_reporting_the_same_device_updates_it_rather_than_duplicating_it()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-9");

        await ReportAsync(credential, Storage());
        await ReportAsync(credential, Storage());
        await ReportAsync(credential, Storage());

        await using var db = _fixture.CreateDbContext();
        var count = await db.UsbDevices.CountAsync(u => u.DeviceId == deviceId);

        count.ShouldBe(1);
    }

    // ---- refusals ----------------------------------------------------------

    [Fact]
    public async Task A_report_without_a_credential_is_unauthorized()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Request(
            AgentProtocol.Routes.Usb, new UsbReport([], DateTimeOffset.UtcNow)));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_report_with_a_forged_credential_is_unauthorized()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Request(
            AgentProtocol.Routes.Usb, new UsbReport([], DateTimeOffset.UtcNow),
            credential: $"{Guid.CreateVersion7():N}.{Guid.CreateVersion7():N}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_report_with_an_unsupported_protocol_version_is_refused()
    {
        var (_, credential, _) = await EnrollAsync("USB-RPT-10");
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Request(
            AgentProtocol.Routes.Usb, new UsbReport([], DateTimeOffset.UtcNow),
            credential, protocolVersion: AgentProtocol.Version + 99));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An absurd device count is refused before anything is written.
    /// </summary>
    [Fact]
    public async Task A_report_claiming_thousands_of_devices_is_refused()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-RPT-11");
        using var client = _fixture.Factory.CreateClient();

        var flood = Enumerable.Range(0, 5000)
            .Select(i => Storage($@"USB\VID_0781&PID_5581\FLOOD{i}"))
            .ToArray();

        var response = await client.SendAsync(Request(
            AgentProtocol.Routes.Usb, new UsbReport(flood, DateTimeOffset.UtcNow), credential));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var db = _fixture.CreateDbContext();
        (await db.UsbDevices.CountAsync(u => u.DeviceId == deviceId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_report_is_valid_and_means_nothing_is_attached()
    {
        var (_, credential, _) = await EnrollAsync("USB-RPT-12");

        var policy = await ReportAsync(credential);

        policy.Grants.ShouldBeEmpty();
    }
}
