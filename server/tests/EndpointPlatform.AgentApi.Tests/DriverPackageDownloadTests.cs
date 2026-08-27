using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Drivers;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The route an endpoint uses to fetch an approved driver archive.
/// </summary>
/// <remarks>
/// <para>
/// The archive is addressed by package id and nothing else: there is no route, and no
/// client method, that fetches a caller-supplied URL. That is what makes "no arbitrary
/// driver package installation" a structural property rather than a validation rule.
/// </para>
/// <para>
/// The transfer itself is not a trust boundary -- the endpoint re-hashes the archive
/// and verifies the catalogue signature regardless -- so what is asserted here is
/// access control: authentication, organization isolation, and withdrawal taking
/// effect immediately.
/// </para>
/// </remarks>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class DriverPackageDownloadTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static Uri Content(Guid packageId) => new(
        AgentProtocol.RoutePrefix + AgentProtocol.Routes.DriverPackages + "/" + packageId
        + AgentProtocol.Routes.PackageContentSuffix,
        UriKind.Relative);

    /// <summary>
    /// Distinct content per package, because the catalogue holds one row per content
    /// hash per organization -- two packages with identical bytes are a duplicate by
    /// design, not two packages.
    /// </summary>
    private static (byte[] Bytes, string Sha) NewArchive()
    {
        var bytes = Encoding.UTF8.GetBytes($"pretend-driver-archive-{Guid.NewGuid():N}");
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private async Task<(Guid DeviceId, string Credential, Guid OrganizationId)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();

        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"drv-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Post(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "DRV-PC", $"machine-{Guid.CreateVersion7():N}", "1.3.0", null)));

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}", org.Id);
    }

    private static HttpRequestMessage Post(string route, object body)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        return message;
    }

    private static HttpRequestMessage Get(Uri uri, string? credential, int? protocolVersion = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, uri);

        message.Headers.Add(
            AgentProtocol.Headers.ProtocolVersion,
            (protocolVersion ?? AgentProtocol.Version).ToString());

        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    private async Task<(Guid PackageId, byte[] Bytes)> SeedPackageAsync(
        Guid organizationId, bool withdrawn = false)
    {
        var (bytes, sha) = NewArchive();

        await using var db = _fixture.CreateDbContext();

        var package = new DriverPackage(
            organizationId, "Contoso NIC", "2.0", "Contoso", sha, "contoso-nic.zip",
            bytes.Length, "contoso.inf", @"PCI\VEN_8086&DEV_1234", "2.0.0.0",
            "Contoso Corporation", Guid.CreateVersion7(), "admin@test");

        if (withdrawn)
        {
            package.Withdraw(DateTimeOffset.UtcNow);
        }

        db.DriverPackages.Add(package);
        await db.SaveChangesAsync();

        var store = _fixture.Factory.Services
            .GetRequiredService<Infrastructure.Software.IPackageContentStore>();

        using (var source = new MemoryStream(bytes))
        {
            await store.SaveAsync(sha, source);
        }

        return (package.Id, bytes);
    }

    [Fact]
    public async Task An_enrolled_device_can_download_an_approved_package()
    {
        var (_, credential, organizationId) = await EnrollAsync();
        var (packageId, bytes) = await SeedPackageAsync(organizationId);

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Get(Content(packageId), credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    [Fact]
    public async Task An_unauthenticated_device_gets_nothing()
    {
        var (_, _, organizationId) = await EnrollAsync();
        var (packageId, _) = await SeedPackageAsync(organizationId);

        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Get(Content(packageId), credential: null)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await client.SendAsync(Get(Content(packageId), "bogus.credential")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Withdrawal takes effect immediately, so a task queued before the decision
    /// cannot still fetch the archive afterwards.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_package_can_no_longer_be_downloaded()
    {
        var (_, credential, organizationId) = await EnrollAsync();
        var (packageId, _) = await SeedPackageAsync(organizationId, withdrawn: true);

        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Get(Content(packageId), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_package_is_not_found()
    {
        var (_, credential, _) = await EnrollAsync();

        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Get(Content(Guid.CreateVersion7()), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_package_whose_content_is_missing_is_not_found_rather_than_a_server_error()
    {
        var (_, credential, organizationId) = await EnrollAsync();

        await using var db = _fixture.CreateDbContext();
        var package = new DriverPackage(
            organizationId, "Ghost", "1.0", null,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()))),
            "ghost.zip", 10, "ghost.inf", @"PCI\VEN_0000&DEV_0000", "1.0.0.0",
            "Contoso Corporation", Guid.CreateVersion7(), "admin@test");

        db.DriverPackages.Add(package);
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Get(Content(package.Id), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_mismatched_protocol_version_is_refused()
    {
        var (_, credential, organizationId) = await EnrollAsync();
        var (packageId, _) = await SeedPackageAsync(organizationId);

        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(Get(Content(packageId), credential, protocolVersion: AgentProtocol.Version + 1)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
