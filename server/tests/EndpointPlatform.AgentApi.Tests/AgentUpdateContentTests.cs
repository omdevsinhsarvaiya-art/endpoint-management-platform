using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Which agent-release bytes a device is allowed to fetch.
/// </summary>
/// <remarks>
/// <para>
/// This route is the fleet gate. Publishing is what decides whether a build may
/// reach machines by itself, and this endpoint is where that decision is enforced
/// against the device asking for it -- so it must serve Published and nothing else.
/// </para>
/// <para>
/// Written because the gate was, until now, entirely untested. The console's
/// download button and this route once shared one service method; when they were
/// separated so an administrator could fetch a Draft to install by hand, nothing in
/// the suite would have noticed if this route had been pointed at the permissive
/// method too. Repointing it passed all 150 tests. That is exactly the mistake
/// worth failing a build over, so it now does.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class AgentUpdateContentTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static Uri Content(Guid releaseId) => new(
        AgentProtocol.RoutePrefix + AgentProtocol.Routes.AgentUpdate + $"/{releaseId}/content",
        UriKind.Relative);

    /// <summary>Creates a release row and its content-store blob directly.</summary>
    private async Task<Guid> SeedReleaseAsync(string version, AgentReleaseStatus status)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"fake-msi-{version}-{Guid.CreateVersion7():N}-" + new string('z', 512));
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await using var db = _fixture.CreateDbContext();

        var release = new AgentRelease(
            version, "windows", "x64", $"EndpointPlatformAgent-{version}-x64.msi",
            sha256, null, null, bytes.LongLength, Guid.CreateVersion7(), "content-tests");

        var now = DateTimeOffset.UtcNow;
        if (status is AgentReleaseStatus.Published or AgentReleaseStatus.Revoked)
        {
            release.Publish(now);
        }

        if (status == AgentReleaseStatus.Revoked)
        {
            release.Revoke(now);
        }

        db.AgentReleases.Add(release);
        await db.SaveChangesAsync();

        // Write the bytes into the same content store the host under test reads
        // from, so this exercises the real streaming path rather than a stub.
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPackageContentStore>();
            using var source = new MemoryStream(bytes);
            await store.SaveAsync(sha256, source);
        }

        return release.Id;
    }

    /// <summary>
    /// A device and its credential, seeded directly.
    /// </summary>
    /// <remarks>
    /// Deliberately not enrolling through the endpoint. Enrollment is rate limited
    /// and that budget is shared by every test in this assembly, so a class that
    /// enrolls once per test starts failing with 429 as the suite grows -- which is
    /// exactly what happened here. Nothing in this class is testing enrollment; it
    /// needs an authenticated device and nothing more.
    /// </remarks>
    private async Task<string> SeedDeviceCredentialAsync()
    {
        await using var db = _fixture.CreateDbContext();

        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        var token = new EnrollmentToken(
            org.Id, $"upd-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "content-tests", DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Domain.Devices.Device.Enroll(
            org.Id, "UPD-PC", $"machine-{Guid.CreateVersion7()}", "1.0.0",
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var secret = SecretGenerator.GenerateSecret();
        db.AgentCredentials.Add(new AgentCredential(
            device.Id, SecretGenerator.GenerateKeyId(), SecretGenerator.HashSecret(secret),
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();

        var credential = await db.AgentCredentials.AsNoTracking()
            .SingleAsync(c => c.DeviceId == device.Id);

        return $"{credential.KeyId}.{secret}";
    }

    private async Task<HttpResponseMessage> FetchAsync(Guid releaseId, string credential)
    {
        using var client = _fixture.Factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, Content(releaseId));
        request.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        request.Headers.Add(AgentProtocol.Headers.Credential, credential);

        return await client.SendAsync(request);
    }

    // ---- the gate ----------------------------------------------------------

    [Fact]
    public async Task An_agent_can_download_a_published_release()
    {
        var credential = await SeedDeviceCredentialAsync();
        var releaseId = await SeedReleaseAsync("7.10.0", AgentReleaseStatus.Published);

        var response = await FetchAsync(releaseId, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The assertion the fleet gate rests on. A Draft is downloadable by an
    /// administrator through the console; it must remain unreachable by a device.
    /// </summary>
    [Fact]
    public async Task An_agent_cannot_download_a_draft_release()
    {
        var credential = await SeedDeviceCredentialAsync();
        var releaseId = await SeedReleaseAsync("7.11.0", AgentReleaseStatus.Draft);

        (await FetchAsync(releaseId, credential))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound,
                "an unpublished build must be invisible to devices, indistinguishable from one that does not exist");
    }

    [Fact]
    public async Task An_agent_cannot_download_a_revoked_release()
    {
        var credential = await SeedDeviceCredentialAsync();
        var releaseId = await SeedReleaseAsync("7.12.0", AgentReleaseStatus.Revoked);

        (await FetchAsync(releaseId, credential)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unauthenticated_agent_cannot_download_anything()
    {
        var releaseId = await SeedReleaseAsync("7.13.0", AgentReleaseStatus.Published);

        using var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, Content(releaseId));
        request.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
