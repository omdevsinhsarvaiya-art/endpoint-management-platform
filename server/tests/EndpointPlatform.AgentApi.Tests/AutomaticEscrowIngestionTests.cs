using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.BitLocker;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Automatic escrow ingestion: what the Agent API will accept, and what it cannot do.
/// </summary>
/// <remarks>
/// <para>
/// The property these tests exist for is that <b>this process cannot read what it
/// stores</b>. It is reachable by every managed endpoint, so it is the last place
/// that should be able to unlock the estate's disks. It is handed the public sealing
/// key and nothing else, and the tests below prove both halves: envelopes go in, and
/// nothing available to this host takes one back out.
/// </para>
/// <para>
/// The rest is refusals. An agent may file a key against itself, for a volume it has
/// reported, for a protector that volume actually has, sealed to the key it was
/// pinned to at enrollment. Every one of those is checked here by violating it.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class AutomaticEscrowIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const string RecoveryPassword =
        "011000-011000-011000-011000-011000-011000-011000-011000";

    // ---- helpers -----------------------------------------------------------

    private static HttpRequestMessage Post(string route, object body, string? credential = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        { Content = JsonContent.Create(body) };

        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    private static HttpRequestMessage Get(string route, string? credential = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative));
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    private sealed record Enrolled(Guid DeviceId, string Credential, string Volume, string Protector);

    /// <summary>
    /// Seeds a device with a pinned credential and one reported volume.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT go through the enrollment endpoint. That endpoint is
    /// rate limited, the budget is shared by every test in this assembly, and a
    /// class that enrolls once per test exhausts it and fails whatever runs next --
    /// which is a property of the test suite, not of the code under test.
    /// <see cref="Enrollment_pins_the_sealing_key_and_makes_the_device_eligible"/>
    /// covers the real enrollment path once, which is where that behaviour belongs.
    /// </remarks>
    private async Task<Enrolled> EnrollWithVolumeAsync(bool pinned = true)
    {
        await using var db = _fixture.CreateDbContext();

        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            org.Id, $"esc-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "admin@test", now.AddHours(1), 1);

        db.EnrollmentTokens.Add(token);

        var device = Domain.Devices.Device.Enroll(
            org.Id, "ESC-PC", $"machine-{Guid.CreateVersion7():N}", "1.4.0", null, token.Id, now);

        db.Devices.Add(device);

        var secret = SecretGenerator.GenerateSecret();
        var credential = new AgentCredential(
            device.Id, SecretGenerator.GenerateKeyId(), SecretGenerator.HashSecret(secret), now);

        if (pinned)
        {
            credential.PinSealingKey(AgentApiPostgresFixture.SealingFingerprint);
        }

        db.AgentCredentials.Add(credential);

        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var protector = Guid.NewGuid().ToString();

        db.DeviceBitLockerVolumes.Add(new Domain.Devices.DeviceBitLockerVolume(
            device.Id, volume, "C:", "pv-1", 0, 1, 1, 100, 7, true, protector, now));

        await db.SaveChangesAsync();

        return new Enrolled(device.Id, $"{credential.KeyId}.{secret}", volume, protector);
    }

    /// <summary>Seals as the endpoint would, to the key the host was configured with.</summary>
    private static string Seal(string password, RSA? key = null, string? fingerprint = null)
    {
        key ??= AgentApiPostgresFixture.SealingKey;

        var dataKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(password);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(dataKey, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        return JsonSerializer.Serialize(new
        {
            scheme = BitLockerSealScheme.HybridRsaV1,
            wrappedKey = Convert.ToBase64String(key.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256)),
            nonce = Convert.ToBase64String(nonce),
            tag = Convert.ToBase64String(tag),
            ciphertext = Convert.ToBase64String(ciphertext),
            keyFingerprint = fingerprint ?? AgentApiPostgresFixture.SealingFingerprint,
        });
    }

    private Task<HttpResponseMessage> EscrowAsync(
        HttpClient client, Enrolled e, string? envelope = null,
        string? volume = null, string? protector = null) =>
        client.SendAsync(Post(AgentProtocol.Routes.BitLockerEscrow,
            new EscrowRecoveryKeyRequest(
                volume ?? e.Volume, protector ?? e.Protector, envelope ?? Seal(RecoveryPassword)),
            e.Credential));

    // ---- the happy path, and what lands in the table -----------------------

    [Fact]
    public async Task A_sealed_envelope_is_accepted_and_stored_as_ciphertext()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await EscrowAsync(client, e);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = (await response.Content.ReadFromJsonAsync<EscrowRecoveryKeyResponse>())!;
        body.Status.ShouldBe("escrowed");

        await using var db = _fixture.CreateDbContext();
        var escrow = await db.BitLockerRecoveryEscrows.SingleAsync(x => x.DeviceId == e.DeviceId);

        escrow.Origin.ShouldBe(BitLockerEscrowOrigin.Automatic);
        escrow.SealScheme.ShouldBe(BitLockerSealScheme.HybridRsaV1);

        // No administrator filed this, and none is invented.
        escrow.EscrowedByUserId.ShouldBeNull();

        // The column holds the envelope and nothing resembling the password.
        escrow.SealedRecoveryPassword.ShouldNotContain(RecoveryPassword);
        escrow.SealedRecoveryPassword.ShouldNotContain("011000");
        escrow.SealedRecoveryPassword.ShouldNotMatch(@"\d{6}-\d{6}");
    }

    /// <summary>
    /// The claim this whole architecture rests on, asserted directly: nothing the
    /// Agent API host has can open an envelope it just stored.
    /// </summary>
    [Fact]
    public async Task The_agent_api_holds_nothing_that_can_decrypt_what_it_stored()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        (await EscrowAsync(client, e)).EnsureSuccessStatusCode();

        var services = _fixture.Factory.Services;

        // No unsealer, no symmetric protector: neither is registered here.
        services.GetService(typeof(IHybridEnvelopeUnsealer)).ShouldBeNull();
        services.GetService(typeof(IRecoveryKeyProtector)).ShouldBeNull();

        // What it does have is public and cannot decrypt.
        var sealing = (IEscrowSealingKeyProvider)services.GetService(typeof(IEscrowSealingKeyProvider))!;
        sealing.IsConfigured.ShouldBeTrue();
        sealing.Fingerprint.ShouldBe(AgentApiPostgresFixture.SealingFingerprint);

        // And the private key genuinely does open it -- so the assertions above
        // are about capability, not about the data being unusable.
        await using var db = _fixture.CreateDbContext();
        var stored = (await db.BitLockerRecoveryEscrows.SingleAsync(x => x.DeviceId == e.DeviceId))
            .SealedRecoveryPassword;

        new RsaHybridEnvelopeUnsealer(
                Convert.ToBase64String(AgentApiPostgresFixture.SealingKey.ExportPkcs8PrivateKey()))
            .Unseal(stored)
            .ShouldBe(RecoveryPassword);
    }

    [Fact]
    public async Task The_agent_api_configuration_carries_no_decryption_key()
    {
        var configuration = (Microsoft.Extensions.Configuration.IConfiguration)
            _fixture.Factory.Services.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))!;

        configuration["RecoveryEscrow:Key"].ShouldBeNullOrWhiteSpace();
        configuration["RecoveryEscrow:SealingPrivateKey"].ShouldBeNullOrWhiteSpace();

        // The guard that keeps it that way, exercised directly.
        Should.NotThrow(() => AgentApiKeyBoundaryGuard.AssertNoDecryptionKeys(configuration));

        await Task.CompletedTask;
    }

    /// <summary>
    /// The one test that goes through the real enrollment endpoint.
    /// </summary>
    /// <remarks>
    /// Everything else in this class seeds its credential directly to stay off the
    /// enrollment rate limiter, so this is what proves the pin is actually
    /// established where the design says it is -- during authenticated enrollment,
    /// bound to the same exchange that issues the credential.
    /// </remarks>
    [Fact]
    public async Task Enrollment_pins_the_sealing_key_and_makes_the_device_eligible()
    {
        string secret;

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            secret = SecretGenerator.GenerateSecret();

            db.EnrollmentTokens.Add(new EnrollmentToken(
                org.Id, $"pin-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
                Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));

            await db.SaveChangesAsync();
        }

        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Post(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "PIN-PC", $"machine-{Guid.CreateVersion7():N}", "1.4.0", null)));

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        // The agent is handed the public key and the fingerprint to pin.
        body.SealingKeyFingerprint.ShouldBe(AgentApiPostgresFixture.SealingFingerprint);
        body.SealingPublicKey.ShouldBe(AgentApiPostgresFixture.SealingPublicKeySpki);

        // And the server records the same pin against the credential it issued.
        await using var verify = _fixture.CreateDbContext();

        var credential = await verify.AgentCredentials
            .SingleAsync(c => c.DeviceId == body.DeviceId && c.RevokedAt == null);

        credential.SealingKeyFingerprint.ShouldBe(AgentApiPostgresFixture.SealingFingerprint);
        credential.IsAutomaticEscrowEligible.ShouldBeTrue();
    }

    // ---- refusals ----------------------------------------------------------

    /// <summary>
    /// The endpoint takes envelopes. A password sent here is not JSON, so it is
    /// refused before anything looks at it -- and there is no field it could have
    /// occupied even if it were.
    /// </summary>
    [Fact]
    public async Task A_plaintext_recovery_password_is_rejected()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await EscrowAsync(client, e, envelope: RecoveryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    /// <summary>Even wrapped in valid JSON, plaintext has nowhere to go.</summary>
    [Fact]
    public async Task A_password_dressed_up_as_an_envelope_is_rejected()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await EscrowAsync(client, e, envelope: JsonSerializer.Serialize(new
        {
            scheme = BitLockerSealScheme.HybridRsaV1,
            wrappedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(RecoveryPassword)),
            nonce = Convert.ToBase64String(new byte[12]),
            tag = Convert.ToBase64String(new byte[16]),
            ciphertext = Convert.ToBase64String(Encoding.UTF8.GetBytes(RecoveryPassword)),
            keyFingerprint = AgentApiPostgresFixture.SealingFingerprint,
        }));

        // The wrapped key is not 384 bytes, so it is not an RSA-3072 wrap.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Post(AgentProtocol.Routes.BitLockerEscrow,
            new EscrowRecoveryKeyRequest(e.Volume, e.Protector, Seal(RecoveryPassword))));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    /// <summary>
    /// The device is resolved from the credential, so naming another machine's
    /// volume escrows nothing -- it simply is not a volume this device reported.
    /// </summary>
    [Fact]
    public async Task A_volume_belonging_to_another_device_is_rejected()
    {
        var mine = await EnrollWithVolumeAsync();
        var theirs = await EnrollWithVolumeAsync();

        using var client = _fixture.Factory.CreateClient();

        var response = await EscrowAsync(client, mine, volume: theirs.Volume, protector: theirs.Protector);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingStoredAsync(mine.DeviceId);
        await AssertNothingStoredAsync(theirs.DeviceId);
    }

    [Fact]
    public async Task A_protector_the_volume_never_reported_is_rejected()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await EscrowAsync(client, e, protector: Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    /// <summary>
    /// The state every device enrolled before automatic escrow is in. It must keep
    /// working for inventory and be refused here until it re-enrolls.
    /// </summary>
    [Fact]
    public async Task A_credential_without_a_pinned_fingerprint_is_rejected()
    {
        // Exactly the state of every device enrolled before automatic escrow: an
        // active credential whose fingerprint column is null.
        var e = await EnrollWithVolumeAsync(pinned: false);

        using var client = _fixture.Factory.CreateClient();
        var response = await EscrowAsync(client, e);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    /// <summary>
    /// Such a device must still be able to report inventory. Automatic escrow is
    /// withheld; management is not.
    /// </summary>
    [Fact]
    public async Task An_unpinned_device_is_reported_as_ineligible_but_still_served()
    {
        var e = await EnrollWithVolumeAsync(pinned: false);
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Get(AgentProtocol.Routes.BitLockerEscrowStatus, e.Credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = (await response.Content.ReadFromJsonAsync<BitLockerEscrowStatusResponse>())!;

        body.Eligible.ShouldBeFalse();
        body.SealingKeyFingerprint.ShouldBeNull();
    }

    [Fact]
    public async Task A_revoked_credential_is_rejected()
    {
        var e = await EnrollWithVolumeAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            foreach (var credential in await db.AgentCredentials
                         .Where(c => c.DeviceId == e.DeviceId && c.RevokedAt == null)
                         .ToListAsync())
            {
                credential.Revoke(DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync();
        }

        using var client = _fixture.Factory.CreateClient();
        var response = await EscrowAsync(client, e);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    /// <summary>
    /// An envelope sealed to somebody else's key. Accepting it would file a
    /// credential this platform can never open, which looks like success and is not.
    /// </summary>
    [Fact]
    public async Task An_envelope_sealed_to_the_wrong_key_is_rejected()
    {
        var e = await EnrollWithVolumeAsync();
        using var attacker = RSA.Create(3072);
        using var client = _fixture.Factory.CreateClient();

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(attacker.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var response = await EscrowAsync(client, e, envelope: Seal(RecoveryPassword, attacker, fingerprint));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"scheme":"aesgcm-v1","wrappedKey":"AA==","nonce":"AA==","tag":"AA==","ciphertext":"AA==","keyFingerprint":"00"}""")]
    [InlineData("""{"scheme":"hybrid-rsa-v1","wrappedKey":"!!!","nonce":"AA==","tag":"AA==","ciphertext":"AA==","keyFingerprint":"00"}""")]
    public async Task A_malformed_envelope_is_rejected(string envelope)
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await EscrowAsync(client, e, envelope: envelope);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertNothingStoredAsync(e.DeviceId);
    }

    // ---- idempotence -------------------------------------------------------

    /// <summary>
    /// Repeated inventory must be free. The second upload is a success reporting
    /// that nothing was needed, not a conflict.
    /// </summary>
    [Fact]
    public async Task Escrowing_the_same_protector_twice_is_idempotent()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var first = await EscrowAsync(client, e);
        var second = await EscrowAsync(client, e);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await first.Content.ReadFromJsonAsync<EscrowRecoveryKeyResponse>())!.Status.ShouldBe("escrowed");

        var repeat = (await second.Content.ReadFromJsonAsync<EscrowRecoveryKeyResponse>())!;
        repeat.Status.ShouldBe("already-escrowed");

        await using var db = _fixture.CreateDbContext();
        var rows = await db.BitLockerRecoveryEscrows.CountAsync(x => x.DeviceId == e.DeviceId);
        rows.ShouldBe(1, "a repeated upload must not create a second row");
    }

    // ---- status ------------------------------------------------------------

    [Fact]
    public async Task Status_reports_escrow_state_without_any_key_material()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        var before = await client.SendAsync(Get(AgentProtocol.Routes.BitLockerEscrowStatus, e.Credential));
        var beforeBody = (await before.Content.ReadFromJsonAsync<BitLockerEscrowStatusResponse>())!;

        beforeBody.Eligible.ShouldBeTrue();
        beforeBody.SealingKeyFingerprint.ShouldBe(AgentApiPostgresFixture.SealingFingerprint);
        beforeBody.Protectors.ShouldHaveSingleItem().Escrowed.ShouldBeFalse();

        (await EscrowAsync(client, e)).EnsureSuccessStatusCode();

        var after = await client.SendAsync(Get(AgentProtocol.Routes.BitLockerEscrowStatus, e.Credential));
        var raw = await after.Content.ReadAsStringAsync();

        (await after.Content.ReadFromJsonAsync<BitLockerEscrowStatusResponse>())!
            .Protectors.ShouldHaveSingleItem().Escrowed.ShouldBeTrue();

        // Metadata only: no envelope, no ciphertext, nothing shaped like a key.
        raw.ShouldNotContain(RecoveryPassword);
        raw.ShouldNotContain("ciphertext");
        raw.ShouldNotContain("wrappedKey");
        raw.ShouldNotMatch(@"\d{6}-\d{6}");
    }

    [Fact]
    public async Task Status_requires_authentication()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Get(AgentProtocol.Routes.BitLockerEscrowStatus)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task Every_ingestion_outcome_is_audited_without_secrets()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        await EscrowAsync(client, e);
        await EscrowAsync(client, e, protector: Guid.NewGuid().ToString());

        await using var db = _fixture.CreateDbContext();

        var rows = await db.AuditLogEntries
            .Where(a => a.Action.StartsWith("bitlocker.recovery_key.auto"))
            .ToListAsync();

        rows.ShouldNotBeEmpty();
        rows.Select(r => r.Action).ShouldContain("bitlocker.recovery_key.auto_escrowed");
        rows.Select(r => r.Action).ShouldContain("bitlocker.recovery_key.auto_escrow_failed");

        var envelope = (await db.BitLockerRecoveryEscrows.SingleAsync(x => x.DeviceId == e.DeviceId))
            .SealedRecoveryPassword;

        foreach (var row in rows)
        {
            var payload = (row.PreviousState ?? "") + (row.NewState ?? "") + (row.FailureReason ?? "");

            payload.ShouldNotContain(RecoveryPassword);
            payload.ShouldNotContain("011000");
            payload.ShouldNotContain(envelope);
            Infrastructure.Auditing.AuditStateRedactor.ContainsSecretShape(payload).ShouldBeFalse();
        }
    }

    // ---- end to end ---------------------------------------------------------

    /// <summary>
    /// The whole chain, with only the Windows reader faked: gate, local sealing,
    /// HTTP, PostgreSQL, and the status the agent reads back.
    /// </summary>
    /// <remarks>
    /// The Windows call is the one thing that cannot be exercised here -- it needs a
    /// real encrypted volume, and no test may retrieve a real recovery password.
    /// Everything after it is real: a genuine gate, genuine RSA and AES-GCM, a
    /// genuine HTTP request to a genuine Agent API host, and a genuine database row.
    /// </remarks>
    [Fact]
    public async Task End_to_end_the_agent_seals_locally_and_the_server_stores_what_it_cannot_read()
    {
        var e = await EnrollWithVolumeAsync();
        using var client = _fixture.Factory.CreateClient();

        // The endpoint half, driven by the real gate.
        var reader = new StubReader(RecoveryPassword);

        var gate = new EndpointAgent.Core.BitLocker.AutomaticEscrowGate(
            reader, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                EndpointAgent.Core.BitLocker.AutomaticEscrowGate>.Instance);

        using var sealingKey = RSA.Create();
        sealingKey.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(AgentApiPostgresFixture.SealingPublicKeySpki), out _);

        var credential = new EndpointAgent.Core.Abstractions.DeviceCredential(
            e.DeviceId, "k", "s", AgentApiPostgresFixture.SealingFingerprint);

        var sealedResult = await gate.TrySealAsync(
            credential, sealingKey, e.Volume, [e.Protector], e.Protector, alreadyEscrowed: false);

        sealedResult.Outcome.ShouldBe(EndpointAgent.Core.BitLocker.AutomaticEscrowOutcome.Sealed);
        reader.Calls.ShouldBe(1, "the success path retrieves the password exactly once");

        // ...and the server half, over real HTTP.
        var upload = await EscrowAsync(client, e, envelope: sealedResult.Envelope!.ToJson());
        upload.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var stored = await db.BitLockerRecoveryEscrows.SingleAsync(x => x.DeviceId == e.DeviceId);

        stored.Origin.ShouldBe(BitLockerEscrowOrigin.Automatic);
        stored.SealScheme.ShouldBe(BitLockerSealScheme.HybridRsaV1);
        stored.SealedRecoveryPassword.ShouldNotContain(RecoveryPassword);

        // The Agent API stored it and cannot open it.
        _fixture.Factory.Services.GetService(typeof(IHybridEnvelopeUnsealer)).ShouldBeNull();

        // Only the private-key holder can, which is what makes the row worth having.
        new RsaHybridEnvelopeUnsealer(
                Convert.ToBase64String(AgentApiPostgresFixture.SealingKey.ExportPkcs8PrivateKey()))
            .Unseal(stored.SealedRecoveryPassword)
            .ShouldBe(RecoveryPassword);

        // And the status the agent reads back now says so, so a second pass would
        // skip this protector without reading anything.
        var status = await client.SendAsync(Get(AgentProtocol.Routes.BitLockerEscrowStatus, e.Credential));
        var body = (await status.Content.ReadFromJsonAsync<BitLockerEscrowStatusResponse>())!;

        var protector = body.Protectors.ShouldHaveSingleItem();
        protector.Escrowed.ShouldBeTrue();
        protector.State.ShouldBe("Escrowed");
        protector.Due.ShouldBeFalse();
    }

    /// <summary>Stands in for Windows. No test retrieves a real recovery password.</summary>
    private sealed class StubReader(string password)
        : EndpointAgent.Core.Abstractions.IRecoveryPasswordReader
    {
        public int Calls { get; private set; }

        public Task<EndpointAgent.Core.Abstractions.RecoveryPasswordReadResult> ReadAsync(
            string volumeDeviceIdentifier, string keyProtectorId, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new EndpointAgent.Core.Abstractions.RecoveryPasswordReadResult(
                EndpointAgent.Core.Abstractions.RecoveryPasswordReadStatus.Success,
                password,
                keyProtectorId));
        }
    }

    private async Task AssertNothingStoredAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();

        (await db.BitLockerRecoveryEscrows.CountAsync(x => x.DeviceId == deviceId))
            .ShouldBe(0, "a refused upload must leave nothing behind");
    }
}
