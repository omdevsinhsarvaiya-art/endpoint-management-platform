using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The Phase 1 enrollment security test matrix, end to end over HTTP against a
/// real database: valid, invalid, expired, revoked, reuse, max-use, duplicate
/// identity, and information-leak checks.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class EnrollmentEndpointTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    // ---------------------------------------------------------------- helpers

    private async Task<(Guid TokenId, string Secret)> IssueTokenAsync(
        int maxUses = 5,
        TimeSpan? lifetime = null,
        bool revoked = false)
    {
        await using var dbContext = _fixture.CreateDbContext();

        var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();

        var token = new EnrollmentToken(
            organization.Id,
            $"test-token-{Guid.CreateVersion7():N}",
            SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(),
            "test-admin@company.local",
            DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(1)),
            maxUses);

        if (revoked)
        {
            token.Revoke(DateTimeOffset.UtcNow);
        }

        dbContext.EnrollmentTokens.Add(token);
        await dbContext.SaveChangesAsync();

        return (token.Id, secret);
    }

    private HttpClient CreateClient() => _fixture.Factory.CreateClient();

    private static EnrollRequest MakeRequest(string tokenSecret, string? machineIdentifier = null) =>
        new(
            tokenSecret,
            "TEST-PC-01",
            machineIdentifier ?? $"machine-{Guid.CreateVersion7():N}",
            "1.0.0",
            "Windows 11 Test");

    private static Task<HttpResponseMessage> PostEnrollAsync(HttpClient client, EnrollRequest request)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll, UriKind.Relative))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        return client.SendAsync(message);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Valid_enrollment_succeeds_and_returns_a_credential()
    {
        var (_, secret) = await IssueTokenAsync();
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EnrollResponse>();
        body.ShouldNotBeNull();
        body.DeviceId.ShouldNotBe(Guid.Empty);
        body.CredentialKeyId.Length.ShouldBe(32);
        body.CredentialSecret.Length.ShouldBe(64);
        body.ReEnrolled.ShouldBeFalse();

        // The device record exists, and the credential is stored hashed - the
        // returned secret must appear nowhere in the database.
        await using var dbContext = _fixture.CreateDbContext();
        var device = await dbContext.Devices.SingleAsync(d => d.Id == body.DeviceId);
        device.Hostname.ShouldBe("TEST-PC-01");

        var credential = await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == body.CredentialKeyId);
        credential.SecretHash.ShouldNotBe(body.CredentialSecret);
        credential.SecretHash.ShouldBe(SecretGenerator.HashSecret(body.CredentialSecret));
    }

    [Fact]
    public async Task An_invalid_token_is_refused()
    {
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (_, secret) = await IssueTokenAsync(lifetime: TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var (_, secret) = await IssueTokenAsync(revoked: true);
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refusals_are_indistinguishable_between_unknown_expired_and_revoked()
    {
        // If the response differed per reason, a caller could probe the token
        // space and learn which guesses were once-valid tokens.
        var (_, expiredSecret) = await IssueTokenAsync(lifetime: TimeSpan.FromMilliseconds(1));
        var (_, revokedSecret) = await IssueTokenAsync(revoked: true);
        await Task.Delay(50);
        using var client = CreateClient();

        var unknown = await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()));
        var expired = await PostEnrollAsync(client, MakeRequest(expiredSecret));
        var revoked = await PostEnrollAsync(client, MakeRequest(revokedSecret));

        unknown.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        expired.StatusCode.ShouldBe(unknown.StatusCode);
        revoked.StatusCode.ShouldBe(unknown.StatusCode);

        // Bodies must be identical except for the per-request correlation id.
        var unknownBody = Normalize(await unknown.Content.ReadAsStringAsync());
        var expiredBody = Normalize(await expired.Content.ReadAsStringAsync());
        var revokedBody = Normalize(await revoked.Content.ReadAsStringAsync());

        expiredBody.ShouldBe(unknownBody, "refusal bodies must not reveal the reason");
        revokedBody.ShouldBe(unknownBody, "refusal bodies must not reveal the reason");

        static string Normalize(string problemJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(problemJson);
            var stripped = document.RootElement.EnumerateObject()
                .Where(p => p.Name != "correlationId")
                .ToDictionary(p => p.Name, p => p.Value.ToString());
            return System.Text.Json.JsonSerializer.Serialize(stripped);
        }
    }

    [Fact]
    public async Task Maximum_use_enforcement_refuses_the_extra_enrollment()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 2);
        using var client = CreateClient();

        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_single_use_token_cannot_be_reused()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 1);
        using var client = CreateClient();

        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostEnrollAsync(client, MakeRequest(secret))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Re_enrolling_the_same_machine_updates_the_device_instead_of_duplicating_it()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        var second = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondBody = (await second.Content.ReadFromJsonAsync<EnrollResponse>())!;

        secondBody.DeviceId.ShouldBe(firstBody.DeviceId, "same machine must map to the same device");
        secondBody.ReEnrolled.ShouldBeTrue();
        secondBody.CredentialKeyId.ShouldNotBe(firstBody.CredentialKeyId, "re-enrollment must rotate the credential");

        await using var dbContext = _fixture.CreateDbContext();

        (await dbContext.Devices.CountAsync(d => d.MachineIdentifier == machineIdentifier))
            .ShouldBe(1, "no duplicate device row");

        var oldCredential = await dbContext.AgentCredentials.SingleAsync(c => c.KeyId == firstBody.CredentialKeyId);
        oldCredential.IsActive.ShouldBeFalse("the old credential must be revoked on re-enrollment");
    }

    [Fact]
    public async Task The_old_credential_stops_authenticating_after_re_enrollment()
    {
        var (_, secret) = await IssueTokenAsync(maxUses: 5);
        var machineIdentifier = $"machine-{Guid.CreateVersion7():N}";
        using var client = CreateClient();

        var first = await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));
        var firstBody = (await first.Content.ReadFromJsonAsync<EnrollResponse>())!;

        await PostEnrollAsync(client, MakeRequest(secret, machineIdentifier));

        // Replay the superseded credential against heartbeat.
        var heartbeat = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Heartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest("TEST-PC-01", "1.0.0", null, DateTimeOffset.UtcNow)),
        };
        heartbeat.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        heartbeat.Headers.Add(
            AgentProtocol.Headers.Credential,
            $"{firstBody.CredentialKeyId}.{firstBody.CredentialSecret}");

        var response = await client.SendAsync(heartbeat);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_protocol_version_is_rejected()
    {
        var (_, secret) = await IssueTokenAsync();
        using var client = CreateClient();

        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll, UriKind.Relative))
        {
            Content = JsonContent.Create(MakeRequest(secret)),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, "999");

        var response = await client.SendAsync(message);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refused_enrollments_are_recorded_in_the_audit_trail()
    {
        using var client = CreateClient();

        await PostEnrollAsync(client, MakeRequest(SecretGenerator.GenerateSecret()) with { Hostname = "PROBE-HOST" });

        await using var dbContext = _fixture.CreateDbContext();
        var denial = await dbContext.AuditLogEntries
            .Where(a => a.Action == "device.enroll"
                        && a.Result == Domain.Auditing.AuditResult.Denied
                        && a.ActorDisplay == "PROBE-HOST")
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();

        denial.ShouldNotBeNull("a refused enrollment attempt is a security signal and must be audited");
        denial.FailureReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task Successful_enrollment_is_recorded_in_the_audit_trail_without_the_secret()
    {
        var (tokenId, secret) = await IssueTokenAsync();
        using var client = CreateClient();

        var response = await PostEnrollAsync(client, MakeRequest(secret));
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        await using var dbContext = _fixture.CreateDbContext();
        var entry = await dbContext.AuditLogEntries
            .Where(a => a.Action == "device.enroll" && a.DeviceId == body.DeviceId)
            .SingleAsync();

        entry.Result.ShouldBe(Domain.Auditing.AuditResult.Success);
        entry.TargetId.ShouldBe(tokenId.ToString());

        // Neither the enrollment token secret nor the credential secret may appear
        // anywhere in the audit entry.
        var serialized = System.Text.Json.JsonSerializer.Serialize(new
        {
            entry.NewState,
            entry.PreviousState,
            entry.FailureReason,
            entry.TargetDisplay,
        });
        serialized.ShouldNotContain(secret);
        serialized.ShouldNotContain(body.CredentialSecret);
    }
}
