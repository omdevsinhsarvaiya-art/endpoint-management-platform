using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// Phase 11: the agent package-content endpoint streams the exact stored bytes to
/// an authenticated device, and refuses everything else.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class PackageContentTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static (byte[] Bytes, string Sha) NewContent()
    {
        var bytes = Encoding.UTF8.GetBytes("MSI-content-" + Guid.CreateVersion7().ToString("N") + new string('x', 256));
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"pkg-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(EnrollReq(secret));
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage EnrollReq(string secret)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll, UriKind.Relative))
        { Content = JsonContent.Create(new EnrollRequest(secret, "PKG-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)) };
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        return m;
    }

    private async Task<(Guid PackageId, string Sha)> SeedPackageAsync(bool withdrawn = false)
    {
        var (bytes, sha) = NewContent();

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.EndpointPlatformDbContext>();
        var packageService = scope.ServiceProvider.GetRequiredService<SoftwarePackageService>();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();

        using var content = new MemoryStream(bytes);
        var result = await packageService.CreateAsync(
            org.Id, "Contoso App", "1.0", "Contoso", sha, "app.msi",
            "{2C4E1D0B-1111-2222-3333-444455556666}", "CN=Contoso", content,
            Guid.CreateVersion7(), "admin");
        result.Status.ShouldBe(PackageCreateStatus.Created);

        if (withdrawn)
        {
            var pkg = await db.SoftwarePackages.SingleAsync(p => p.Id == result.Package!.Id);
            pkg.Withdraw(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        return (result.Package!.Id, sha);
    }

    private HttpRequestMessage ContentReq(Guid packageId, string? credential)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, new Uri(
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Packages + "/" + packageId + AgentProtocol.Routes.PackageContentSuffix,
            UriKind.Relative));
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    [Fact]
    public async Task An_authenticated_device_gets_the_exact_bytes()
    {
        var (_, credential) = await EnrollAsync();
        var (packageId, sha) = await SeedPackageAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(ContentReq(packageId, credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(SHA256.HashData(downloaded)).ShouldBe(sha);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_rejected()
    {
        var (packageId, _) = await SeedPackageAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(ContentReq(packageId, credential: null)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_withdrawn_package_is_not_downloadable()
    {
        var (_, credential) = await EnrollAsync();
        var (packageId, _) = await SeedPackageAsync(withdrawn: true);
        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(ContentReq(packageId, credential)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_package_is_not_found()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.SendAsync(ContentReq(Guid.CreateVersion7(), credential)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
