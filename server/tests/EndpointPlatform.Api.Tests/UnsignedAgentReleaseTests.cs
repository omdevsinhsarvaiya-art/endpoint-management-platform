using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
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
/// These tests record the platform behaviour as it actually is, including the part
/// that is uncomfortable: <b>nothing on the server refuses to publish an unsigned
/// release</b>, and the agent installs a published unsigned build on hash
/// verification alone. That is a deliberate documented stance rather than an
/// oversight, but it means Draft status is the only thing standing between an
/// unsigned build and every device -- so these tests pin the boundary at exactly
/// that line, rather than asserting a signing gate that does not exist.
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

    /// <summary>Uploads a release, optionally naming a signer. Returns id and true SHA-256.</summary>
    private static async Task<(Guid Id, string Sha256)> UploadAsync(
        HttpClient client, string version, string? signerSubject = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"fake-msi-{version}-{Guid.CreateVersion7():N}-" + new string('x', 2048));

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
    /// Recorded because it is the load-bearing fact, not because it is desirable.
    /// </summary>
    /// <remarks>
    /// The server applies no signing requirement to publishing, and the agent
    /// installs a published unsigned release after verifying its SHA-256 only,
    /// logging a warning. So Draft status is the entire safeguard. If a signing
    /// gate is ever added, this test should fail and be replaced -- deliberately,
    /// rather than a console quietly implying a protection that was never there.
    /// </remarks>
    [Fact]
    public async Task Publishing_is_what_makes_a_release_deployable_and_signing_does_not_gate_it()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.44.0");
        var deviceId = await SeedDeviceAsync("1.0.0");

        // Before publishing: refused.
        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Publishing an unsigned build succeeds -- there is no signing gate.
        (await client.PostAsync(Release(releaseId, "publish"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // ...and it immediately becomes targetable.
        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>Revoking an unsigned release withdraws it like any other.</summary>
    [Fact]
    public async Task A_revoked_unsigned_release_stops_being_deployable()
    {
        using var client = await AdminAsync();

        var (releaseId, _) = await UploadAsync(client, "8.45.0");
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

        var (releaseId, _) = await UploadAsync(client, "8.46.0", "CN=Example Corp");
        (await client.PostAsync(Release(releaseId, "publish"), null)).EnsureSuccessStatusCode();

        var deviceId = await SeedDeviceAsync("8.47.0");

        (await client.PostAsJsonAsync(UpdateAgent(deviceId), new { releaseId }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId)).ShouldBe(0);
    }

    /// <summary>The listing carries the signer as reported, null included.</summary>
    [Fact]
    public async Task The_listing_reports_signed_and_unsigned_releases_distinguishably()
    {
        using var client = await AdminAsync();

        var (unsignedId, _) = await UploadAsync(client, "8.48.0");
        var (signedId, _) = await UploadAsync(client, "8.49.0", "CN=Example Corp");

        var body = await client.GetStringAsync(new Uri("/admin/v1/agent-releases/", UriKind.Relative));
        using var document = System.Text.Json.JsonDocument.Parse(body);

        var rows = document.RootElement.EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("id").GetString()!,
                e => e.GetProperty("signerSubject").ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : e.GetProperty("signerSubject").GetString());

        rows[unsignedId.ToString()].ShouldBeNull();
        rows[signedId.ToString()].ShouldBe("CN=Example Corp");
    }
}
