using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// BitLocker inventory ingestion.
/// </summary>
/// <remarks>
/// <para>
/// Two themes. The first is that a failed query must never be absorbed as good news:
/// an <c>AccessDenied</c> report leaves the last known volumes intact and records why
/// they are stale, rather than emptying the table and letting a console show an
/// encrypted machine as having no encrypted volumes.
/// </para>
/// <para>
/// The second is that nothing resembling a recovery key can reach the database, even
/// if an agent tried to send one. The protector column takes GUIDs and rejects
/// everything else, so it cannot be used as a smuggling route into a field an
/// operator will read.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class BitLockerIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const int FullyDecrypted = 0;
    private const int FullyEncrypted = 1;
    private const int ProtectionOff = 0;
    private const int ProtectionOn = 1;

    /// <summary>The shape of a real BitLocker recovery password: six groups of six digits.</summary>
    private const string LooksLikeARecoveryKey =
        "123456-654321-111111-222222-333333-444444-555555-666666";

    private static InventoryReport Report(InventoryBitLocker? bitLocker) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, bitLocker);

    private static InventoryBitLockerVolume Volume(
        string letter = "C:",
        int? conversion = FullyEncrypted,
        int? protection = ProtectionOn,
        int? type = 0,
        int? percentage = 100,
        IReadOnlyList<string>? protectorIds = null,
        string? deviceId = null) =>
        new(
            DeviceIdentifier: deviceId ?? $"\\\\?\\Volume{{{Guid.NewGuid()}}}\\",
            DriveLetter: letter,
            PersistentVolumeId: "pv-" + Guid.NewGuid().ToString("N"),
            VolumeType: type,
            ConversionStatus: conversion,
            ProtectionStatus: protection,
            EncryptionPercentage: percentage,
            EncryptionMethod: 7,
            HasRecoveryPasswordProtector: true,
            RecoveryProtectorIds: protectorIds ?? [Guid.NewGuid().ToString()]);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"bl-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "BL-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
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
        HttpClient client, string credential, string status, params InventoryBitLockerVolume[] volumes) =>
        client.SendAsync(Req(AgentProtocol.Routes.Inventory,
            Report(new InventoryBitLocker(status, volumes)), credential));

    // ---- persistence -------------------------------------------------------

    [Fact]
    public async Task Volumes_persist_and_replace_wholesale()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available", Volume("C:"), Volume("D:", type: 1));

        await using (var db = _fixture.CreateDbContext())
        {
            var rows = await db.DeviceBitLockerVolumes.Where(v => v.DeviceId == deviceId).ToListAsync();
            rows.Count.ShouldBe(2);

            var os = rows.Single(v => v.DriveLetter == "C:");
            os.ConversionStatus.ShouldBe(FullyEncrypted);
            os.ProtectionStatus.ShouldBe(ProtectionOn);
            os.EncryptionPercentage.ShouldBe(100);
            os.HasRecoveryPasswordProtector.ShouldBe(true);

            (await db.DeviceBitLockerStatus.SingleAsync(s => s.DeviceId == deviceId))
                .Availability.ShouldBe(BitLockerAvailability.Available);
        }

        (await UploadAsync(client, credential, "Available", Volume("C:")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.DeviceBitLockerVolumes.CountAsync(v => v.DeviceId == deviceId)).ShouldBe(1);
        }
    }

    /// <summary>
    /// The case the separate availability row exists for. An agent that loses its
    /// elevation must not wipe the encryption picture and leave the estate looking
    /// unencrypted.
    /// </summary>
    [Fact]
    public async Task A_refused_query_records_why_and_keeps_the_last_known_volumes()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available", Volume("C:"));
        await UploadAsync(client, credential, "AccessDenied");

        await using var db = _fixture.CreateDbContext();

        (await db.DeviceBitLockerVolumes.CountAsync(v => v.DeviceId == deviceId))
            .ShouldBe(1, "a failed query must not delete what was previously known");

        (await db.DeviceBitLockerStatus.SingleAsync(s => s.DeviceId == deviceId))
            .Availability.ShouldBe(BitLockerAvailability.AccessDenied);
    }

    [Theory]
    [InlineData("AccessDenied", BitLockerAvailability.AccessDenied)]
    [InlineData("NotAvailable", BitLockerAvailability.NotAvailable)]
    [InlineData("Error", BitLockerAvailability.Error)]
    [InlineData("Available", BitLockerAvailability.Available)]
    public async Task Availability_round_trips(string reported, BitLockerAvailability expected)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, reported);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceBitLockerStatus.SingleAsync(s => s.DeviceId == deviceId))
            .Availability.ShouldBe(expected);
    }

    /// <summary>
    /// An unrecognised status must not be trusted as Available, or a malformed report
    /// would have its volume list believed.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("available")]
    [InlineData("Encrypted")]
    [InlineData("Unknown")]
    public async Task An_unrecognised_status_is_stored_as_unknown(string reported)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, reported, Volume("C:"));

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceBitLockerStatus.SingleAsync(s => s.DeviceId == deviceId))
            .Availability.ShouldBe(BitLockerAvailability.Unknown);

        (await db.DeviceBitLockerVolumes.CountAsync(v => v.DeviceId == deviceId))
            .ShouldBe(0, "volumes from a report we do not understand are not stored");
    }

    [Fact]
    public async Task A_duplicate_volume_identifier_is_stored_once()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available",
            Volume("C:", deviceId: "\\\\?\\Volume{same}\\"),
            Volume("D:", deviceId: "\\\\?\\Volume{same}\\"));

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceBitLockerVolumes.CountAsync(v => v.DeviceId == deviceId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_oversized_payload_is_capped()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var flood = Enumerable.Range(0, 500)
            .Select(i => Volume($"V{i}", deviceId: $"\\\\?\\Volume{{flood-{i}}}\\"))
            .ToArray();

        (await UploadAsync(client, credential, "Available", flood)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceBitLockerVolumes.CountAsync(v => v.DeviceId == deviceId))
            .ShouldBe(Infrastructure.Devices.DeviceInventoryService.MaxBitLockerVolumes);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task An_impossible_encryption_percentage_is_stored_as_unknown(int percentage)
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available", Volume("C:", percentage: percentage));

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceBitLockerVolumes.SingleAsync(v => v.DeviceId == deviceId))
            .EncryptionPercentage.ShouldBeNull();
    }

    [Fact]
    public async Task An_unreadable_volume_keeps_its_nulls_rather_than_defaulting()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available",
            Volume("C:", conversion: null, protection: null, percentage: null));

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceBitLockerVolumes.SingleAsync(v => v.DeviceId == deviceId);

        row.ConversionStatus.ShouldBeNull();
        row.ProtectionStatus.ShouldBeNull();
        row.EncryptionPercentage.ShouldBeNull();

        BitLockerPosture.ClassifyVolume(row.ConversionStatus, row.ProtectionStatus)
            .ShouldBe(BitLockerVolumeState.Unknown);
    }

    [Fact]
    public async Task An_agent_that_reports_no_bitlocker_section_changes_nothing()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available", Volume("C:"));

        (await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(null), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceBitLockerVolumes.CountAsync(v => v.DeviceId == deviceId)).ShouldBe(1);
        (await db.DeviceBitLockerStatus.SingleAsync(s => s.DeviceId == deviceId))
            .Availability.ShouldBe(BitLockerAvailability.Available);
    }

    // ---- secrets -----------------------------------------------------------

    /// <summary>
    /// The protector column takes GUIDs and nothing else, so an agent that tried to
    /// put a recovery password there — through compromise or a coding mistake — finds
    /// the value rejected rather than persisted somewhere an operator would read it.
    /// </summary>
    [Fact]
    public async Task A_protector_field_containing_a_recovery_key_shape_is_rejected()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available",
            Volume("C:", protectorIds: [LooksLikeARecoveryKey]));

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceBitLockerVolumes.SingleAsync(v => v.DeviceId == deviceId);

        row.RecoveryProtectorIds.ShouldBeNull();
    }

    [Fact]
    public async Task Well_formed_protector_ids_are_kept_and_malformed_ones_dropped()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var real = Guid.NewGuid().ToString();

        await UploadAsync(client, credential, "Available",
            Volume("C:", protectorIds: [real, LooksLikeARecoveryKey, "not-a-guid"]));

        await using var db = _fixture.CreateDbContext();
        var stored = (await db.DeviceBitLockerVolumes.SingleAsync(v => v.DeviceId == deviceId))
            .RecoveryProtectorIds;

        stored.ShouldBe(real);
        stored!.ShouldNotContain("123456");
    }

    /// <summary>
    /// A sweep of everything the ingestion path persisted for this device, checked
    /// against the recovery-key shape. Broader than the column-level test above
    /// because a future field could reintroduce the risk somewhere else.
    /// </summary>
    [Fact]
    public async Task No_stored_bitlocker_field_carries_anything_shaped_like_a_recovery_key()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available",
            Volume("C:", protectorIds: [LooksLikeARecoveryKey]),
            Volume("D:", type: 1, protectorIds: [LooksLikeARecoveryKey]));

        await using var db = _fixture.CreateDbContext();
        var rows = await db.DeviceBitLockerVolumes.Where(v => v.DeviceId == deviceId).ToListAsync();

        foreach (var row in rows)
        {
            var everything = string.Join('|',
                row.DeviceIdentifier, row.DriveLetter, row.PersistentVolumeId, row.RecoveryProtectorIds);

            everything.ShouldNotContain("123456");
            System.Text.RegularExpressions.Regex.IsMatch(everything, @"\d{6}-\d{6}")
                .ShouldBeFalse("a recovery-password shape reached storage");
        }
    }

    /// <summary>
    /// BitLocker ingestion writes no audit rows of its own, so there is no audit
    /// payload in which a key or a credential could appear.
    /// </summary>
    [Fact]
    public async Task Ingestion_writes_no_audit_record_that_could_carry_a_secret()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await UploadAsync(client, credential, "Available", Volume("C:"));
        await UploadAsync(client, credential, "Available", Volume("C:", conversion: FullyDecrypted,
            protection: ProtectionOff, percentage: 0));

        await using var db = _fixture.CreateDbContext();
        var entries = await db.AuditLogEntries
            .Where(a => a.DeviceId == deviceId)
            .ToListAsync();

        foreach (var entry in entries)
        {
            var payload = (entry.PreviousState ?? "") + (entry.NewState ?? "");
            payload.ShouldNotContain(credential);
            System.Text.RegularExpressions.Regex.IsMatch(payload, @"\d{6}-\d{6}").ShouldBeFalse();
        }
    }
}
