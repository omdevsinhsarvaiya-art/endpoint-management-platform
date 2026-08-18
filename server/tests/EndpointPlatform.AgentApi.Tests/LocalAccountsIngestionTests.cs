using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>Phase 4 read-side: local accounts flow through inventory ingestion.</summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class LocalAccountsIngestionTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private static InventoryReport ReportWith(InventoryLocalAccounts? accounts) =>
        new(
            new InventoryHardware(null, null, null, null, null, null, null, []),
            [],
            null,
            DateTimeOffset.UtcNow,
            accounts);

    private static InventoryLocalAccounts SampleAccounts() => new(
        [
            new InventoryLocalUser(
                "S-1-5-21-111-222-333-500", "Administrator", null, "Built-in account",
                Enabled: false, PasswordRequired: true, PasswordExpires: false, null,
                IsLocalAdministrator: true),
            new InventoryLocalUser(
                "S-1-5-21-111-222-333-1001", "jsmith", "John Smith", null,
                Enabled: true, PasswordRequired: true, PasswordExpires: true,
                DateTimeOffset.UtcNow.AddDays(-1), IsLocalAdministrator: false),
        ],
        [
            new InventoryLocalGroup(
                "S-1-5-32-544", "Administrators", "Full control",
                [new InventoryGroupMember("Administrator", "S-1-5-21-111-222-333-500", "User")]),
        ]);

    private async Task<(Guid DeviceId, string Credential)> EnrollAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();
        dbContext.EnrollmentTokens.Add(new EnrollmentToken(
            organization.Id, $"la-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await dbContext.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(NewRequest(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, "LA-PC", $"machine-{Guid.CreateVersion7():N}", "1.0.0", null)));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}");
    }

    private static HttpRequestMessage NewRequest(string route, object body, string? credential = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    [Fact]
    public async Task Local_accounts_persist_with_admin_flags_and_membership()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, ReportWith(SampleAccounts()), credential));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _fixture.CreateDbContext();

        var users = await dbContext.DeviceLocalUsers.Where(u => u.DeviceId == deviceId).ToListAsync();
        users.Count.ShouldBe(2);
        users.Single(u => u.Sid.EndsWith("-500", StringComparison.Ordinal))
            .IsLocalAdministrator.ShouldBeTrue();
        users.Single(u => u.Name == "jsmith").Enabled.ShouldBeTrue();

        var groups = await dbContext.DeviceLocalGroups.Where(g => g.DeviceId == deviceId).ToListAsync();
        groups.Count.ShouldBe(1);
        groups[0].IsAdministratorsGroup.ShouldBeTrue();
        groups[0].MemberCount.ShouldBe(1);
        groups[0].MembersJson.ShouldContain("S-1-5-21-111-222-333-500");
    }

    [Fact]
    public async Task A_second_upload_replaces_the_account_snapshot()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, ReportWith(SampleAccounts()), credential));

        var reduced = new InventoryLocalAccounts(
            [new InventoryLocalUser("S-1-5-21-111-222-333-1002", "newuser", null, null, true, true, true, null, false)],
            []);
        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, ReportWith(reduced), credential));

        await using var dbContext = _fixture.CreateDbContext();
        var users = await dbContext.DeviceLocalUsers.Where(u => u.DeviceId == deviceId).ToListAsync();
        users.Count.ShouldBe(1, "the snapshot must be replaced, not merged");
        users[0].Name.ShouldBe("newuser");
        (await dbContext.DeviceLocalGroups.CountAsync(g => g.DeviceId == deviceId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_upload_without_the_section_keeps_the_previous_snapshot()
    {
        var (deviceId, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, ReportWith(SampleAccounts()), credential));
        await client.SendAsync(NewRequest(AgentProtocol.Routes.Inventory, ReportWith(null), credential));

        await using var dbContext = _fixture.CreateDbContext();
        (await dbContext.DeviceLocalUsers.CountAsync(u => u.DeviceId == deviceId))
            .ShouldBe(2, "a report omitting the section must not delete known accounts");
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-XYZ")]
    [InlineData("")]
    public async Task A_malformed_sid_is_rejected_with_400(string sid)
    {
        var (_, credential) = await EnrollAsync();
        using var client = _fixture.Factory.CreateClient();

        var accounts = new InventoryLocalAccounts(
            [new InventoryLocalUser(sid, "user", null, null, true, true, true, null, false)],
            []);

        var response = await client.SendAsync(
            NewRequest(AgentProtocol.Routes.Inventory, ReportWith(accounts), credential));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
