using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Phase 11: registering, listing and deploying software packages. Registration
/// and deployment require software.deploy; listing requires software.view; the
/// content-hash gate is enforced at upload.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class PackageEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Packages = new("/admin/v1/packages/", UriKind.Relative);

    private static (byte[] Bytes, string Sha) MakeContent()
    {
        var bytes = Encoding.UTF8.GetBytes("msi-" + Guid.CreateVersion7().ToString("N") + new string('z', 300));
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static MultipartFormDataContent UploadBody(byte[] bytes, string declaredSha)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { file, "file", "app.msi" },
            { new StringContent("Contoso App"), "name" },
            { new StringContent("1.0.0"), "version" },
            { new StringContent("Contoso"), "publisher" },
            { new StringContent(declaredSha), "sha256" },
            { new StringContent("{2C4E1D0B-1111-2222-3333-444455556666}"), "msiProductCode" },
            { new StringContent("CN=Contoso"), "requiredSignerSubject" },
        };
    }

    [Fact]
    public async Task Super_admin_can_register_a_package_with_a_matching_hash()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        var (bytes, sha) = MakeContent();

        var response = await client.PostAsync(Packages, UploadBody(bytes, sha));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_mismatched_hash_is_rejected()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        var (bytes, _) = MakeContent();

        var response = await client.PostAsync(Packages, UploadBody(bytes, new string('b', 64)));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_auditor_cannot_register_but_can_list()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);
        var (bytes, sha) = MakeContent();

        (await client.PostAsync(Packages, UploadBody(bytes, sha)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.GetAsync(Packages)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deploying_to_a_device_queues_an_install_task()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        var (bytes, sha) = MakeContent();

        var create = await client.PostAsync(Packages, UploadBody(bytes, sha));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var packageId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var deviceId = await SeedDeviceAsync();

        var deploy = await client.PostAsJsonAsync(
            new Uri($"/admin/v1/packages/{packageId}/deploy", UriKind.Relative),
            new { deviceId });
        deploy.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallPackage))
            .ShouldBe(1);
    }

    private async Task<Guid> SeedDeviceAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var token = new EnrollmentToken(org.Id, $"dep-{Guid.CreateVersion7():N}",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "DEP-PC", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }
}
