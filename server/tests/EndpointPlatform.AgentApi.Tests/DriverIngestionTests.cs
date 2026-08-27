using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Driver inventory ingestion: what an endpoint reports, and what the server makes
/// of it.
/// </summary>
/// <remarks>
/// <para>
/// The ingestion path is where a hostile or broken agent meets the database, so the
/// tests here lean on the payloads a well-behaved agent would never send: duplicate
/// instance ids, an implausible problem code, more devices than any real machine
/// has. Each must be absorbed without distorting the fault counts an operator will
/// act on.
/// </para>
/// <para>
/// The audit assertions matter for a different reason. Drivers are re-reported on
/// every inventory cycle, so an audit row per upload would bury real transitions
/// under thousands of identical ones. Only a change in the fault set is recorded.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class DriverIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport Report(IReadOnlyList<InventoryDriver>? drivers) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, null, null, null, null, drivers);

    private static InventoryDriver Driver(
        string name, int? problemCode, string? instanceId = null, bool? signed = true) =>
        new(
            InstanceId: instanceId ?? $"PCI\\VEN_8086&DEV_{name}",
            DeviceName: name,
            DeviceClass: "System",
            Manufacturer: "Contoso",
            DriverProvider: "Contoso Inc",
            DriverVersion: "10.1.2.3",
            DriverDate: DateTimeOffset.UtcNow.AddYears(-2),
            InfName: "oem12.inf",
            ProblemCode: problemCode,
            IsSigned: signed);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"drv-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "DRV-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage Req(string route, object body, string? credential = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        { Content = JsonContent.Create(body) };
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    private Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string credential, IReadOnlyList<InventoryDriver>? drivers) =>
        client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(drivers), credential));

    // ---- persistence -------------------------------------------------------

    [Fact]
    public async Task Drivers_persist_and_replace_wholesale()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("nic", 0), Driver("gpu", 0)]);

        await using (var db = _fixture.CreateDbContext())
        {
            var rows = await db.DeviceDrivers.Where(d => d.DeviceId == deviceId).ToListAsync();
            rows.Count.ShouldBe(2);

            var nic = rows.Single(d => d.DeviceName == "nic");
            nic.DriverProvider.ShouldBe("Contoso Inc");
            nic.DriverVersion.ShouldBe("10.1.2.3");
            nic.InfName.ShouldBe("oem12.inf");
            nic.IsSigned.ShouldBe(true);
            nic.ProblemCode.ShouldBe(0);
        }

        var second = await UploadAsync(client, credential, [Driver("nic", 0)]);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.DeviceDrivers.CountAsync(d => d.DeviceId == deviceId)).ShouldBe(1);
        }
    }

    /// <summary>
    /// An agent that cannot determine a fact reports null, and null must survive the
    /// round trip. Defaulting either of these would turn "we do not know" into a
    /// claim: a healthy device, or an unsigned driver.
    /// </summary>
    [Fact]
    public async Task Unknown_problem_state_and_unknown_signing_survive_as_null()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("mystery", null, signed: null)]);

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceDrivers.SingleAsync(d => d.DeviceId == deviceId);

        row.ProblemCode.ShouldBeNull();
        row.IsSigned.ShouldBeNull();
    }

    /// <summary>
    /// The instance id is the devnode's identity, so a repeat is a malformed payload
    /// rather than two devices. Left unchecked it would let one upload inflate the
    /// fault counts arbitrarily.
    /// </summary>
    [Fact]
    public async Task A_duplicate_instance_id_is_stored_once()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential,
        [
            Driver("gpu", 28, instanceId: "PCI\\SAME"),
            Driver("gpu-again", 28, instanceId: "PCI\\SAME"),
            Driver("gpu-yet-again", 28, instanceId: "pci\\same"),
        ]);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceDrivers.CountAsync(d => d.DeviceId == deviceId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_driver_with_no_instance_id_is_dropped_rather_than_stored_nameless()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("ok", 0), Driver("bad", 0, instanceId: "  ")]);

        await using var db = _fixture.CreateDbContext();
        var rows = await db.DeviceDrivers.Where(d => d.DeviceId == deviceId).ToListAsync();

        rows.Count.ShouldBe(1);
        rows.Single().DeviceName.ShouldBe("ok");
    }

    /// <summary>
    /// A CM_PROB_* value is a small positive integer. Anything else did not come
    /// from Windows, and storing it would let an agent invent problem codes that the
    /// classifier would then report as real unattributed faults.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(999_999)]
    public async Task An_implausible_problem_code_is_stored_as_unknown(int code)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("liar", code)]);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceDrivers.SingleAsync(d => d.DeviceId == deviceId)).ProblemCode.ShouldBeNull();
    }

    [Fact]
    public async Task An_oversized_payload_is_capped_rather_than_stored_whole()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var flood = Enumerable.Range(0, 5000)
            .Select(i => Driver($"d{i}", 0, instanceId: $"PCI\\FLOOD{i}"))
            .ToList();

        (await UploadAsync(client, credential, flood)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var stored = await db.DeviceDrivers.CountAsync(d => d.DeviceId == deviceId);

        stored.ShouldBe(Infrastructure.Devices.DeviceInventoryService.MaxDrivers);
    }

    /// <summary>
    /// An agent that predates this section omits it, and the server keeps whatever
    /// it last knew rather than treating the omission as "no drivers".
    /// </summary>
    [Fact]
    public async Task An_agent_that_reports_no_driver_section_leaves_the_stored_snapshot_alone()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("nic", 0)]);
        (await UploadAsync(client, credential, null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceDrivers.CountAsync(d => d.DeviceId == deviceId)).ShouldBe(1);
    }

    // ---- audit -------------------------------------------------------------

    private async Task<List<Domain.Auditing.AuditLogEntry>> DriverAuditAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AuditLogEntries
            .Where(a => a.Action == "driver.problem.detected" && a.DeviceId == deviceId)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();
    }

    /// <summary>
    /// The first report is the arrival of evidence, not a change in the machine.
    /// Auditing it would make every enrollment look like a new fault.
    /// </summary>
    [Fact]
    public async Task The_first_driver_report_is_not_audited_as_a_change()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("gpu", 28)]);

        (await DriverAuditAsync(deviceId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_newly_faulted_device_is_audited_once_and_not_again()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("nic", 0), Driver("gpu", 0)]);
        await UploadAsync(client, credential, [Driver("nic", 0), Driver("gpu", 28)]);

        var afterFault = await DriverAuditAsync(deviceId);
        afterFault.Count.ShouldBe(1);
        afterFault.Single().Result.ShouldBe(Domain.Auditing.AuditResult.Failure);

        // Same fault, reported again on the next cycle: nothing new to say.
        await UploadAsync(client, credential, [Driver("nic", 0), Driver("gpu", 28)]);
        await UploadAsync(client, credential, [Driver("nic", 0), Driver("gpu", 28)]);

        (await DriverAuditAsync(deviceId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_fault_clearing_is_audited_as_a_success()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("gpu", 0)]);
        await UploadAsync(client, credential, [Driver("gpu", 28)]);
        await UploadAsync(client, credential, [Driver("gpu", 0)]);

        var entries = await DriverAuditAsync(deviceId);
        entries.Count.ShouldBe(2);
        entries[^1].Result.ShouldBe(Domain.Auditing.AuditResult.Success);
    }

    /// <summary>
    /// One faulted device swapped for another leaves the overall state at Problem
    /// while something material has changed, so the fault set is what is compared.
    /// </summary>
    [Fact]
    public async Task A_different_device_failing_is_audited_even_though_the_state_is_unchanged()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("gpu", 0), Driver("nic", 0)]);
        await UploadAsync(client, credential, [Driver("gpu", 28), Driver("nic", 0)]);
        await UploadAsync(client, credential, [Driver("gpu", 0), Driver("nic", 28)]);

        (await DriverAuditAsync(deviceId)).Count.ShouldBe(2);
    }

    /// <summary>
    /// USB storage restriction (Milestone 11a) disables devices, which reports
    /// CM_PROB_DISABLED. Restricting a device must not raise a driver fault alarm.
    /// </summary>
    [Fact]
    public async Task A_device_being_disabled_is_not_audited_as_a_driver_fault()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("stick", 0)]);
        await UploadAsync(client, credential, [Driver("stick", 22)]);

        (await DriverAuditAsync(deviceId)).ShouldBeEmpty();
    }

    /// <summary>
    /// Nothing in a driver payload is a secret, and the audit record must stay that
    /// way: it carries counts, instance ids and problem codes, never a credential.
    /// </summary>
    [Fact]
    public async Task The_audit_record_carries_no_credential_material()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, [Driver("gpu", 0)]);
        await UploadAsync(client, credential, [Driver("gpu", 28)]);

        var entry = (await DriverAuditAsync(deviceId)).Single();
        var payload = (entry.PreviousState ?? "") + (entry.NewState ?? "");

        payload.ShouldNotContain(credential);
        payload.ShouldContain("problemCode");
    }
}
