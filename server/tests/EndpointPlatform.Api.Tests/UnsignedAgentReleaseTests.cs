using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// What registering an unsigned agent release does, and does not, entitle it to.
/// </summary>
/// <remarks>
/// <para>
/// The 1.4.1 build exists as an unsigned MSI that was installed by hand and never
/// registered. Registering it is safe; publishing it is the consequential act, and
/// the two must stay clearly separate.
/// </para>
/// <para>
/// These tests once recorded an uncomfortable fact: nothing on the server refused
/// to publish an unsigned release, so Draft status was the only thing between an
/// unsigned build and every device. That gap is now closed. Publishing re-verifies
/// the stored artifact -- hash, Authenticode signature, chain, code-signing EKU and
/// the configured publisher -- and refuses anything that fails, whoever is asking.
/// The tests below pin both halves: registering an unsigned build stays harmless,
/// and publishing one is impossible.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class UnsignedAgentReleaseTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri Releases => new("/admin/v1/agent-releases", UriKind.Relative);
    private static Uri Release(Guid id, string action) =>
        new($"/admin/v1/agent-releases/{id}/{action}", UriKind.Relative);
    private static Uri UpdateAgent(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/actions/update-agent", UriKind.Relative);

    private async Task<HttpClient> AdminAsync()
    {
        var email = $"rel-{Guid.CreateVersion7():N}@test.local";

        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Release Admin");
            user.SetPasswordHash(
                PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = SecretGenerator.GenerateSecret();

        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
            db.AdminSessions.Add(new AdminSession(
                user.Id, SecretGenerator.HashSecret(token), user.SecurityStamp,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null, userAgent: "unsigned-release-tests"));
            await db.SaveChangesAsync();
        }

        return _fixture.CreateClientFor(token);
    }

    /// <summary>
    /// Uploads a release. <paramref name="signed"/> selects an artifact signed by
    /// the fixture authority; otherwise an unsigned MSI-shaped file. A typed
    /// <paramref name="signerSubject"/> is still sent, to prove the server ignores it.
    /// Returns id and the true SHA-256 of what was sent.
    /// </summary>
    private static async Task<(Guid Id, string Sha256)> UploadAsync(
        HttpClient client, string version, string? signerSubject = null, bool signed = false)
    {
        var seed = $"{version}-{Guid.CreateVersion7():N}";
        var bytes = signed
            ? TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: seed)
            : TestArtifacts.UnsignedMsi(seed);

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var form = new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
            { new StringContent(sha256), "sha256" },
        };

        if (signerSubject is not null)
        {
            form.Add(new StringContent(signerSubject), "signerSubject");
        }

        var response = await client.PostAsync(Releases, form);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();
        var id = Guid.Parse(System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("releaseId").GetString()!);

        return (id, sha256);
    }

    private async Task<Guid> SeedDeviceAsync(string agentVersion)
    {
        await using var db = _fixture.CreateDbContext();

        var orgId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();

        var token = new EnrollmentToken(
            orgId, $"rel-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            Guid.CreateVersion7(), "release-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            orgId, "REL-PC", $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return device.Id;
    }

    // ---- registering is safe ----------------------------------------------

    /// <summary>
    /// An unsigned MSI can be registered. This is what makes 1.4.1 visible in the
    /// console at all, and it must not require inventing a signer.
    /// </summary>
    [Fact]
    public async Task An_unsigned_release_can_be_registered_and_records_no_signer()
    {
        using var client = await AdminAsync();

        var (id, sha256) = await UploadAsync(client, "8.41.0");

        await using var db = _fixture.CreateDbContext();
        var release = await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == id);

        release.SignerSubject.ShouldBeNull("no signature must be invented for an unsigned build");
        release.Sha256.ShouldBe(sha256, "the recorded hash is the hash of the uploaded bytes");
        release.Status.ShouldBe(Domain.Agents.AgentReleaseStatus.Draft);
    }

    /// <summary>
    /// The property the whole change turns on: uploading does not deploy. A freshly
    /// registered build is a Draft, and a Draft is refused by the update endpoint.
    /// </summary>
    [Fact]
    public async Task Registering_an_unsigned_release_does_not_make_it_a_fleet_target()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.42.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId))
            .ShouldBe(0, "an unpublished release must not queue an update");
    }

    /// <summary>
    /// The upload path verifies the bytes rather than trusting the claim, so a
    /// release row can never describe content it does not hold.
    /// </summary>
    [Fact]
    public async Task A_release_whose_bytes_do_not_match_the_claimed_hash_is_refused()
    {
        using var client = await AdminAsync();

        var bytes = System.Text.Encoding.UTF8.GetBytes("real-bytes-" + new string('y', 2048));

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var form = new MultipartFormDataContent
        {
            { file, "file", "EndpointPlatformAgent-8.43.0-x64.msi" },
            { new StringContent("8.43.0"), "version" },
            { new StringContent(new string('0', 64)), "sha256" },
        };

        (await client.PostAsync(Releases, form)).StatusCode.ShouldNotBe(HttpStatusCode.Created);

        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.CountAsync(r => r.Version == "8.43.0")).ShouldBe(0);
    }

    // ---- where the real boundary is ---------------------------------------

    /// <summary>
    /// The gate. This test previously asserted the opposite -- that an unsigned
    /// build could be published and immediately targeted -- and was kept on purpose
    /// as a record of the gap. It now asserts the gap is closed, and its old body
    /// is the reason the gate exists.
    /// </summary>
    [Fact]
    public async Task An_unsigned_release_cannot_be_published_and_so_never_becomes_deployable()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.44.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        // Registering was fine; publishing is refused, with a reason.
        var publish = await client.PostAsync(Release(releaseId, "publish"), null);
        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await publish.Content.ReadAsStringAsync()).ShouldContain("verification");

        // Still a draft, still not a target.
        await using (var db = _fixture.CreateDbContext())
        {
            (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
                .Status.ShouldBe(Domain.Agents.AgentReleaseStatus.Draft);
        }

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>A signed build by the expected publisher publishes and deploys.</summary>
    [Fact]
    public async Task A_signed_release_by_the_expected_publisher_publishes_and_becomes_deployable()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.55.0", signed: true);
        var deviceId = await SeedDeviceAsync("1.0.0");

        (await client.PostAsync(Release(releaseId, "publish"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>Revoking an unsigned release withdraws it like any other.</summary>
    [Fact]
    public async Task A_revoked_release_stops_being_deployable()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.45.0", signed: true);
        (await client.PostAsync(Release(releaseId, "publish"), null)).EnsureSuccessStatusCode();
        (await client.PostAsync(Release(releaseId, "revoke"), null)).EnsureSuccessStatusCode();

        var deviceId = await SeedDeviceAsync("1.0.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId)).ShouldBe(0);
    }

    // ---- a device ahead of the fleet --------------------------------------

    /// <summary>
    /// The controlled device runs 1.4.1 while the newest published build is older.
    /// It must be refused a downgrade by the server, independently of anything the
    /// console decides to show.
    /// </summary>
    [Fact]
    public async Task A_device_ahead_of_the_published_release_is_refused_a_downgrade()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.46.0", signed: true);
        (await client.PostAsync(Release(releaseId, "publish"), null)).EnsureSuccessStatusCode();

        var deviceId = await SeedDeviceAsync("8.47.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId)).ShouldBe(0);
    }

    // ---- downloading, which is not deploying -------------------------------

    /// <summary>
    /// A Draft can be downloaded by an administrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console reused the agent's content path for its own download button, so
    /// a build could not be retrieved until it had been published -- which for an
    /// unsigned build is exactly backwards, since publishing is what makes it
    /// installable on every device. Downloading is one authenticated administrator
    /// fetching an artifact to install by hand.
    /// </para>
    /// <para>
    /// Asserts the bytes, not just the status code: the point of the endpoint is
    /// that it returns the real MSI.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_draft_release_can_be_downloaded_by_an_administrator()
    {
        using var client = await AdminAsync();

        var (releaseId, sha256) = await UploadAsync(client, "8.50.0");

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
                .Status.ShouldBe(Domain.Agents.AgentReleaseStatus.Draft, "precondition");
        }

        var response = await client.GetAsync(Release(releaseId, "download"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(SHA256.HashData(bytes))
            .ShouldBe(sha256, "the download must return exactly the stored artifact");
    }

    [Fact]
    public async Task A_published_release_can_still_be_downloaded()
    {
        using var client = await AdminAsync();

        var (releaseId, sha256) = await UploadAsync(client, "8.51.0", signed: true);
        (await client.PostAsync(Release(releaseId, "publish"), null)).EnsureSuccessStatusCode();

        var response = await client.GetAsync(Release(releaseId, "download"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        Convert.ToHexStringLower(SHA256.HashData(await response.Content.ReadAsByteArrayAsync()))
            .ShouldBe(sha256);
    }

    /// <summary>
    /// Revoked stays undownloadable. "Nothing may download or install it any more"
    /// is the documented lifecycle rule, and widening the download path must not
    /// have become a way around it.
    /// </summary>
    [Fact]
    public async Task A_revoked_release_cannot_be_downloaded()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.52.0");
        (await client.PostAsync(Release(releaseId, "revoke"), null)).EnsureSuccessStatusCode();

        (await client.GetAsync(Release(releaseId, "download")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Downloadable must not mean deployable. The same Draft that just streamed its
    /// bytes is still refused by the update endpoint.
    /// </summary>
    [Fact]
    public async Task Downloading_a_draft_does_not_make_it_a_fleet_target()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.53.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        (await client.GetAsync(Release(releaseId, "download")))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "the artifact is retrievable");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict, "but it is still not deployable");

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId)).ShouldBe(0);
    }

    /// <summary>Download still requires authentication.</summary>
    [Fact]
    public async Task An_unauthenticated_caller_cannot_download_a_draft()
    {
        using var owner = await AdminAsync();
        var (releaseId, _) = await UploadAsync(owner, "8.54.0");

        using var anonymous = _fixture.Factory.CreateClient();

        (await anonymous.GetAsync(Release(releaseId, "download")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The listing reports the signer the server verified. A signer typed into the
    /// upload is ignored: an unsigned artifact stays unsigned whatever the form said,
    /// and a signed one is attributed to its actual certificate.
    /// </summary>
    [Fact]
    public async Task The_listing_reports_the_verified_signer_never_a_typed_one()
    {
        using var client = await AdminAsync();

        var (unsignedId, _) = await UploadAsync(client, "8.48.0", signerSubject: "CN=Example Corp");
        var (signedId, _) = await UploadAsync(client, "8.49.0", signed: true);

        var body = await client.GetStringAsync(new Uri("/admin/v1/agent-releases/", UriKind.Relative));
        using var document = System.Text.Json.JsonDocument.Parse(body);

        var rows = document.RootElement.EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("id").GetString()!,
                e => e.GetProperty("signerSubject").ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : e.GetProperty("signerSubject").GetString());

        rows[unsignedId.ToString()].ShouldBeNull("a typed signer on an unsigned artifact is discarded");
        rows[signedId.ToString()].ShouldNotBeNull();
        rows[signedId.ToString()]!.ShouldContain(AdminApiPostgresFixture.ExpectedSignerSubject);
    }
}
