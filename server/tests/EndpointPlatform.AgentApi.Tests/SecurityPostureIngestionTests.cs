using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>Phase 12: security posture flows through ingestion and scores.</summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class SecurityPostureIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport Report(InventorySecurityPosture? posture) =>
        new(new InventoryHardware(null, null, null, null, null, null, null, []),
            [], null, DateTimeOffset.UtcNow, null, null, posture);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"sec-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "SEC-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
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
    public async Task Posture_persists_and_scores()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        // All compliant except BitLocker unknown (unelevated).
        var posture = new InventorySecurityPosture(
            true, true, 2, true, true, true, true, true, true, "2.0", null, 1);

        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(posture), credential));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var row = await db.DeviceSecurityPosture.SingleAsync(p => p.DeviceId == deviceId);
        row.DefenderAntivirusEnabled.ShouldBe(true);
        row.BitLockerSystemDriveStatus.ShouldBeNull();
        row.ComplianceScore().ShouldBe(100, "unknown BitLocker is excluded, everything else passes");
    }

    [Fact]
    public async Task Out_of_range_posture_values_are_rejected()
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var posture = new InventorySecurityPosture(
            true, true, 999999, true, true, true, true, true, true, "2.0", "On", 1);

        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Inventory, Report(posture), credential));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
