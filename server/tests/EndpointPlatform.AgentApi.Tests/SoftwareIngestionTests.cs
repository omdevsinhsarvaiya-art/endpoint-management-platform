using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>Phase 7: software inventory flows through ingestion, replace-wholesale.</summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class SoftwareIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport Report(IReadOnlyList<InventorySoftware>? software) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, software);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"sw-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "SW-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
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

    [Fact]
    public async Task Software_persists_and_replaces_wholesale()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(
        [
            new InventorySoftware("Google Chrome", "120.0", "Google LLC", "20260101", @"C:\Program Files\Google", "x64"),
            new InventorySoftware("7-Zip", "23.01", "Igor Pavlov", null, null, "x64"),
        ]), credential));

        await using (var db = _fixture.CreateDbContext())
        {
            var apps = await db.DeviceSoftware.Where(s => s.DeviceId == deviceId).ToListAsync();
            apps.Count.ShouldBe(2);
            apps.Single(a => a.Name == "Google Chrome").Publisher.ShouldBe("Google LLC");
        }

        // Second upload with one app replaces the set.
        var r2 = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(
            [new InventorySoftware("Mozilla Firefox", "121.0", "Mozilla", null, null, "x64")]), credential));
        r2.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var db = _fixture.CreateDbContext())
        {
            var apps = await db.DeviceSoftware.Where(s => s.DeviceId == deviceId).ToListAsync();
            apps.Count.ShouldBe(1);
            apps[0].Name.ShouldBe("Mozilla Firefox");
        }
    }

    [Fact]
    public async Task An_omitted_software_section_keeps_the_previous_snapshot()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(
            [new InventorySoftware("Keeper App", "1.0", null, null, null, null)]), credential));
        await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(null), credential));

        await using var db = _fixture.CreateDbContext();
        (await db.DeviceSoftware.CountAsync(s => s.DeviceId == deviceId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_software_entry_with_no_name_is_rejected()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(
            [new InventorySoftware("", "1.0", null, null, null, null)]), credential));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
