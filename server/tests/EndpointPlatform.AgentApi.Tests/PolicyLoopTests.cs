using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>Phase 6: the policy desired-state loop over real HTTP + PostgreSQL.</summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class PolicyLoopTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private async Task<(Guid DeviceId, string Credential, Guid OrgId)> EnrollAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(org.Id, $"pol-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret), Guid.CreateVersion7(), "admin@test",
            DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var resp = await client.SendAsync(Req(AgentProtocol.Routes.Enroll,
            new EnrollRequest(secret, "POL-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}", org.Id);
    }

    private static HttpRequestMessage Req(string route, object? body = null, string? credential = null, HttpMethod? method = null)
    {
        var m = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative));
        if (body is not null) m.Content = JsonContent.Create(body);
        m.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null) m.Headers.Add(AgentProtocol.Headers.Credential, credential);
        return m;
    }

    private async Task<Guid> CreateAndAssignPolicyAsync(Guid orgId, Guid deviceId, int maxSeconds)
    {
        await using var db = _fixture.CreateDbContext();
        var policy = new Policy(orgId, PolicyType.ScreenLockTimeout, "Lock 10m", "desc");
        policy.AddVersion($$"""{"maxTimeoutSeconds":{{maxSeconds}}}""", DateTimeOffset.UtcNow);
        db.Policies.Add(policy);
        db.PolicyAssignments.Add(new PolicyAssignment(orgId, policy.Id, PolicyAssignmentTarget.Device, deviceId));
        await db.SaveChangesAsync();
        return policy.Id;
    }

    [Fact]
    public async Task Heartbeat_signals_policies_pending_after_assignment()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var before = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("POL-PC", "1.0.0", null, DateTimeOffset.UtcNow), credential));
        (await before.Content.ReadFromJsonAsync<HeartbeatResponse>())!.PoliciesPending.ShouldBeFalse();

        await CreateAndAssignPolicyAsync(orgId, deviceId, 600);

        var after = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("POL-PC", "1.0.0", null, DateTimeOffset.UtcNow), credential));
        (await after.Content.ReadFromJsonAsync<HeartbeatResponse>())!.PoliciesPending.ShouldBeTrue();
    }

    [Fact]
    public async Task Agent_fetches_policies_reports_compliance_and_pending_clears()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        var policyId = await CreateAndAssignPolicyAsync(orgId, deviceId, 600);
        using var client = _fixture.Factory.CreateClient();

        var fetch = await client.SendAsync(Req(AgentProtocol.Routes.Policies, credential: credential, method: HttpMethod.Get));
        var policies = (await fetch.Content.ReadFromJsonAsync<AgentPolicyListResponse>())!;
        var policy = policies.Policies.ShouldHaveSingleItem();
        policy.PolicyId.ShouldBe(policyId);
        policy.Type.ShouldBe("ScreenLockTimeout");

        var report = new AgentPolicyComplianceReport(
        [
            new AgentPolicyComplianceItem(policy.PolicyId, policy.PolicyVersionId, policy.VersionNumber, "Compliant", []),
        ]);
        var post = await client.SendAsync(Req(
            AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix, report, credential));
        post.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await db.PolicyComplianceResults.SingleAsync(r => r.DeviceId == deviceId && r.PolicyId == policyId);
            result.State.ShouldBe(PolicyComplianceState.Compliant);
        }

        var hb = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("POL-PC", "1.0.0", null, DateTimeOffset.UtcNow), credential));
        (await hb.Content.ReadFromJsonAsync<HeartbeatResponse>())!.PoliciesPending.ShouldBeFalse();
    }

    [Fact]
    public async Task A_new_policy_version_makes_compliance_pending_again()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        var policyId = await CreateAndAssignPolicyAsync(orgId, deviceId, 600);
        using var client = _fixture.Factory.CreateClient();

        var fetch = await client.SendAsync(Req(AgentProtocol.Routes.Policies, credential: credential, method: HttpMethod.Get));
        var p = (await fetch.Content.ReadFromJsonAsync<AgentPolicyListResponse>())!.Policies.Single();
        await client.SendAsync(Req(AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix,
            new AgentPolicyComplianceReport([new AgentPolicyComplianceItem(p.PolicyId, p.PolicyVersionId, p.VersionNumber, "Compliant", [])]),
            credential));

        await using (var db = _fixture.CreateDbContext())
        {
            var policy = await db.Policies.SingleAsync(x => x.Id == policyId);
            policy.AddVersion("""{"maxTimeoutSeconds":300}""", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var hb = await client.SendAsync(Req(AgentProtocol.Routes.Heartbeat,
            new HeartbeatRequest("POL-PC", "1.0.0", null, DateTimeOffset.UtcNow), credential));
        (await hb.Content.ReadFromJsonAsync<HeartbeatResponse>())!.PoliciesPending
            .ShouldBeTrue("a new version needs re-evaluation");
    }

    [Fact]
    public async Task Compliance_for_an_unassigned_policy_is_ignored()
    {
        var (deviceId, credential, orgId) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var report = new AgentPolicyComplianceReport(
        [
            new AgentPolicyComplianceItem(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "Compliant", []),
        ]);
        var post = await client.SendAsync(Req(
            AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix, report, credential));
        post.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var db = _fixture.CreateDbContext();
        (await db.PolicyComplianceResults.CountAsync(r => r.DeviceId == deviceId))
            .ShouldBe(0, "results for unassigned policies must be dropped");
    }
}
