using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Ingestion of TPM and TPM+PIN startup-protector observation.
/// </summary>
/// <remarks>
/// <para>
/// The point of these tests is not that the new columns fill in -- that is one test.
/// It is that the three protector kinds stay in three places all the way from the
/// agent payload to the database, so that the automatic recovery-key escrow runner,
/// which reads <c>RecoveryProtectorIds</c> and nothing else, can never be handed the
/// id of a protector that has no recovery password behind it.
/// </para>
/// <para>
/// A startup PIN is not stored, transported or storable here. There is no field for
/// one on the contract and no column for one in the table, and Windows offers no API
/// that would return one, so the tests assert the shape of what is carried rather
/// than trying to prove the absence of a value that has nowhere to live.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class StartupProtectorIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const int FullyEncrypted = 1;
    private const int ProtectionOn = 1;
    private const int OperatingSystemVolume = 0;

    private static InventoryReport Report(InventoryBitLocker? bitLocker) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, bitLocker);

    private static InventoryBitLockerVolume Volume(
        IReadOnlyList<string>? recoveryIds = null,
        IReadOnlyList<string>? tpmIds = null,
        IReadOnlyList<string>? tpmPinIds = null) =>
        new(
            DeviceIdentifier: $"\\\\?\\Volume{{{Guid.NewGuid()}}}\\",
            DriveLetter: "C:",
            PersistentVolumeId: "pv-" + Guid.NewGuid().ToString("N"),
            VolumeType: OperatingSystemVolume,
            ConversionStatus: FullyEncrypted,
            ProtectionStatus: ProtectionOn,
            EncryptionPercentage: 100,
            EncryptionMethod: 7,
            HasRecoveryPasswordProtector: recoveryIds is { Count: > 0 },
            RecoveryProtectorIds: recoveryIds,
            HasTpmProtector: tpmIds is { Count: > 0 },
            TpmProtectorIds: tpmIds,
            HasTpmPinProtector: tpmPinIds is { Count: > 0 },
            TpmPinProtectorIds: tpmPinIds);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"sp-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "SP-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
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

    private async Task UploadAsync(Guid _, string credential, params InventoryBitLockerVolume[] volumes)
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Req(AgentProtocol.Routes.Inventory,
            Report(new InventoryBitLocker("Available", volumes)), credential)))
            .EnsureSuccessStatusCode();
    }

    // ---- the columns fill in ----------------------------------------------

    [Fact]
    public async Task Startup_protectors_are_stored_in_their_own_columns()
    {
        var (deviceId, credential) = await EnrollAsync();

        var recoveryId = Guid.NewGuid().ToString();
        var tpmId = Guid.NewGuid().ToString();
        var tpmPinId = Guid.NewGuid().ToString();

        await UploadAsync(deviceId, credential, Volume([recoveryId], [tpmId], [tpmPinId]));

        await using var db = _fixture.CreateDbContext();
        var volume = await db.DeviceBitLockerVolumes.AsNoTracking()
            .SingleAsync(v => v.DeviceId == deviceId);

        volume.HasRecoveryPasswordProtector.ShouldBe(true);
        volume.RecoveryProtectorIds.ShouldBe(recoveryId);

        volume.HasTpmProtector.ShouldBe(true);
        volume.TpmProtectorIds.ShouldBe(tpmId);

        volume.HasTpmPinProtector.ShouldBe(true);
        volume.TpmPinProtectorIds.ShouldBe(tpmPinId);
    }

    /// <summary>
    /// An agent that does not report startup protectors -- every version before this
    /// feature -- leaves them null rather than false. Absence of a report is not a
    /// report of absence.
    /// </summary>
    [Fact]
    public async Task An_agent_that_reports_no_startup_protectors_leaves_them_unknown()
    {
        var (deviceId, credential) = await EnrollAsync();

        await UploadAsync(deviceId, credential, new InventoryBitLockerVolume(
            DeviceIdentifier: $"\\\\?\\Volume{{{Guid.NewGuid()}}}\\",
            DriveLetter: "C:",
            PersistentVolumeId: null,
            VolumeType: OperatingSystemVolume,
            ConversionStatus: FullyEncrypted,
            ProtectionStatus: ProtectionOn,
            EncryptionPercentage: 100,
            EncryptionMethod: 7,
            HasRecoveryPasswordProtector: true,
            RecoveryProtectorIds: [Guid.NewGuid().ToString()]));

        await using var db = _fixture.CreateDbContext();
        var volume = await db.DeviceBitLockerVolumes.AsNoTracking()
            .SingleAsync(v => v.DeviceId == deviceId);

        volume.HasTpmProtector.ShouldBeNull();
        volume.HasTpmPinProtector.ShouldBeNull();
        volume.TpmPinProtectorIds.ShouldBeNull();

        // ...and the recovery protector it did report is unaffected.
        volume.HasRecoveryPasswordProtector.ShouldBe(true);
    }

    // ---- the separation that matters ---------------------------------------

    /// <summary>
    /// The test this file exists for.
    /// </summary>
    /// <remarks>
    /// Automatic escrow acts on every id in <c>RecoveryProtectorIds</c>, and for each
    /// one the agent is allowed to ask Windows for a 48-digit recovery password. A
    /// TPM+PIN protector has no such password. If its id leaked into this column the
    /// runner would make that call against the wrong protector -- so the column is
    /// asserted to contain the recovery id and nothing else, by exact value.
    /// </remarks>
    [Fact]
    public async Task A_tpm_pin_protector_id_never_reaches_the_recovery_protector_column()
    {
        var (deviceId, credential) = await EnrollAsync();

        var recoveryId = Guid.NewGuid().ToString();
        var tpmId = Guid.NewGuid().ToString();
        var tpmPinId = Guid.NewGuid().ToString();

        await UploadAsync(deviceId, credential, Volume([recoveryId], [tpmId], [tpmPinId]));

        await using var db = _fixture.CreateDbContext();
        var volume = await db.DeviceBitLockerVolumes.AsNoTracking()
            .SingleAsync(v => v.DeviceId == deviceId);

        volume.RecoveryProtectorIds.ShouldBe(recoveryId);
        volume.RecoveryProtectorIds!.ShouldNotContain(tpmPinId);
        volume.RecoveryProtectorIds!.ShouldNotContain(tpmId);
    }

    /// <summary>
    /// The same separation from the other direction: a device whose only startup
    /// protector is TPM+PIN, and which has no recovery protector at all, must present
    /// automatic escrow with nothing to do rather than with a type-4 id to chew on.
    /// </summary>
    [Fact]
    public async Task A_volume_with_only_a_tpm_pin_protector_offers_escrow_no_targets()
    {
        var (deviceId, credential) = await EnrollAsync();

        await UploadAsync(deviceId, credential,
            Volume(recoveryIds: null, tpmIds: null, tpmPinIds: [Guid.NewGuid().ToString()]));

        await using var db = _fixture.CreateDbContext();
        var volume = await db.DeviceBitLockerVolumes.AsNoTracking()
            .SingleAsync(v => v.DeviceId == deviceId);

        volume.HasTpmPinProtector.ShouldBe(true);

        // Nothing for the escrow runner to target.
        volume.RecoveryProtectorIds.ShouldBeNullOrEmpty();
        volume.HasRecoveryPasswordProtector.ShouldBe(false);
    }

    // ---- old agents must not overwrite an observation --------------------

    /// <summary>
    /// An agent that predates this feature must never turn a configured device into
    /// an unconfigured one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inventory upload replaces a device volume rows wholesale, so an older agent --
    /// which omits the startup-protector fields entirely -- rewrites the row of a
    /// machine that was previously observed with a TPM+PIN protector. The only safe
    /// outcome is that the observation degrades to unknown.
    /// </para>
    /// <para>
    /// The outcome to prevent is that it degrades to <c>false</c>. False renders as
    /// "not configured", which would list a correctly protected machine as needing
    /// remediation -- and in a later phase would offer an operator a destructive
    /// protector swap on a device that already complies. Absence of a report is not a
    /// report of absence, and the whole estate reports nothing until a Phase 1 agent
    /// is rolled out.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_older_agent_cannot_overwrite_an_observation_with_a_false_negative()
    {
        var (deviceId, credential) = await EnrollAsync();

        var volumeId = $"\\\\?\\Volume{{{Guid.NewGuid()}}}\\";

        // 1. A Phase 1 agent observes a TPM+PIN protector.
        await UploadAsync(deviceId, credential, new InventoryBitLockerVolume(
            DeviceIdentifier: volumeId,
            DriveLetter: "C:",
            PersistentVolumeId: null,
            VolumeType: OperatingSystemVolume,
            ConversionStatus: FullyEncrypted,
            ProtectionStatus: ProtectionOn,
            EncryptionPercentage: 100,
            EncryptionMethod: 7,
            HasRecoveryPasswordProtector: true,
            RecoveryProtectorIds: [Guid.NewGuid().ToString()],
            HasTpmProtector: false,
            TpmProtectorIds: null,
            HasTpmPinProtector: true,
            TpmPinProtectorIds: [Guid.NewGuid().ToString()]));

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.DeviceBitLockerVolumes.AsNoTracking().SingleAsync(v => v.DeviceId == deviceId))
                .HasTpmPinProtector.ShouldBe(true, "precondition: the device was observed configured");
        }

        // 2. The same device then reports through an older agent, which has no such
        //    fields to send. Same volume id, so this replaces the row above.
        await UploadAsync(deviceId, credential, new InventoryBitLockerVolume(
            DeviceIdentifier: volumeId,
            DriveLetter: "C:",
            PersistentVolumeId: null,
            VolumeType: OperatingSystemVolume,
            ConversionStatus: FullyEncrypted,
            ProtectionStatus: ProtectionOn,
            EncryptionPercentage: 100,
            EncryptionMethod: 7,
            HasRecoveryPasswordProtector: true,
            RecoveryProtectorIds: [Guid.NewGuid().ToString()]));

        await using var after = _fixture.CreateDbContext();
        var volume = await after.DeviceBitLockerVolumes.AsNoTracking()
            .SingleAsync(v => v.DeviceId == deviceId);

        // The assertion that matters: null, and specifically not false.
        volume.HasTpmPinProtector.ShouldBeNull(
            "an agent that cannot report startup protectors must leave the observation "
            + "unknown, never assert their absence");

        volume.HasTpmPinProtector.ShouldNotBe(false);
        volume.HasTpmProtector.ShouldBeNull();
        volume.TpmPinProtectorIds.ShouldBeNull();

        // ...and the posture evaluator reads that as a blind spot, not a finding.
        var posture = Domain.Devices.BitLockerPosture.Evaluate(
            Domain.Devices.BitLockerAvailability.Available,
            [volume.ToView()],
            tpmPresent: true,
            tpmEnabled: true);

        posture.TpmPin.ShouldBe(Domain.Devices.TpmPinObservation.Unknown);
        posture.TpmPin.ShouldNotBe(Domain.Devices.TpmPinObservation.NotConfigured);
    }

    /// <summary>
    /// Reporting a TPM+PIN protector must not create, alter or trigger any escrow
    /// record. Counted across the whole table so a stray row anywhere is caught, not
    /// merely one attached to this device.
    /// </summary>
    [Fact]
    public async Task Reporting_a_startup_protector_creates_no_escrow_state()
    {
        var (deviceId, credential) = await EnrollAsync();

        int escrowsBefore, attemptsBefore;
        await using (var before = _fixture.CreateDbContext())
        {
            escrowsBefore = await before.BitLockerRecoveryEscrows.CountAsync();
            attemptsBefore = await before.BitLockerEscrowAttempts.CountAsync();
        }

        await UploadAsync(deviceId, credential,
            Volume([Guid.NewGuid().ToString()], [Guid.NewGuid().ToString()], [Guid.NewGuid().ToString()]));

        await using var after = _fixture.CreateDbContext();

        (await after.BitLockerRecoveryEscrows.CountAsync())
            .ShouldBe(escrowsBefore, "observing a startup protector must not escrow anything");

        (await after.BitLockerEscrowAttempts.CountAsync())
            .ShouldBe(attemptsBefore, "observing a startup protector must not start an escrow attempt");
    }
}
