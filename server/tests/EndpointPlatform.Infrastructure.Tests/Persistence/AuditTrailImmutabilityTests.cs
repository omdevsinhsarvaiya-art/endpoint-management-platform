using System.Net;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// Proves the audit trail is append-only against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// These are the tests that matter most in this project. An audit trail that can be
/// edited is worse than no audit trail, because it produces confident but false
/// evidence. Both defensive layers are verified independently: the in-process
/// interceptor, and the database trigger that still applies when the interceptor is
/// absent or bypassed.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditTrailImmutabilityTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task An_audit_entry_can_be_inserted()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(dbContext);

        var entry = BuildEntry(organization.Id, "test.insert_succeeds");
        dbContext.AuditLogEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.AuditLogEntries.SingleOrDefaultAsync(a => a.Id == entry.Id);
        stored.ShouldNotBeNull();
        stored.Action.ShouldBe("test.insert_succeeds");
    }

    [Fact]
    public async Task The_interceptor_rejects_an_update_before_it_reaches_the_database()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(dbContext);

        var entry = BuildEntry(organization.Id, "test.interceptor_blocks_update");
        dbContext.AuditLogEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        // Force a modification through the change tracker.
        dbContext.Entry(entry).Property(nameof(AuditLogEntry.Action)).CurrentValue = "test.tampered";
        dbContext.Entry(entry).State = EntityState.Modified;

        var exception = await Should.ThrowAsync<AuditTrailViolationException>(
            () => dbContext.SaveChangesAsync());

        exception.AuditEntryId.ShouldBe(entry.Id);
        exception.AttemptedState.ShouldBe(nameof(EntityState.Modified));
    }

    [Fact]
    public async Task The_interceptor_rejects_a_delete_before_it_reaches_the_database()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(dbContext);

        var entry = BuildEntry(organization.Id, "test.interceptor_blocks_delete");
        dbContext.AuditLogEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        dbContext.AuditLogEntries.Remove(entry);

        await Should.ThrowAsync<AuditTrailViolationException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// The interceptor is only a convenience. This removes it entirely and confirms
    /// the database refuses the write regardless - which is what protects the trail
    /// from an attacker holding the application's database credential.
    /// </summary>
    [Fact]
    public async Task The_database_trigger_rejects_an_update_even_without_the_interceptor()
    {
        await using var seedContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(seedContext);
        var entry = BuildEntry(organization.Id, "test.trigger_blocks_update");
        seedContext.AuditLogEntries.Add(entry);
        await seedContext.SaveChangesAsync();

        await using var unguarded = _fixture.CreateDbContextWithoutAuditGuard();

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            unguarded.Database.ExecuteSqlRawAsync(
                "UPDATE endpoint_platform.audit_log_entries SET action = 'tampered' WHERE id = {0};",
                entry.Id));

        exception.MessageText.ShouldContain("append-only");
        exception.MessageText.ShouldContain("UPDATE");
    }

    [Fact]
    public async Task The_database_trigger_rejects_a_delete_even_without_the_interceptor()
    {
        await using var seedContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(seedContext);
        var entry = BuildEntry(organization.Id, "test.trigger_blocks_delete");
        seedContext.AuditLogEntries.Add(entry);
        await seedContext.SaveChangesAsync();

        await using var unguarded = _fixture.CreateDbContextWithoutAuditGuard();

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            unguarded.Database.ExecuteSqlRawAsync(
                "DELETE FROM endpoint_platform.audit_log_entries WHERE id = {0};",
                entry.Id));

        exception.MessageText.ShouldContain("append-only");
        exception.MessageText.ShouldContain("DELETE");
    }

    /// <summary>
    /// TRUNCATE does not fire row-level triggers. Without a dedicated statement-level
    /// trigger, "delete the audit log" simply becomes "truncate the audit log".
    /// </summary>
    [Fact]
    public async Task The_database_trigger_rejects_truncate()
    {
        await using var seedContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(seedContext);
        seedContext.AuditLogEntries.Add(BuildEntry(organization.Id, "test.trigger_blocks_truncate"));
        await seedContext.SaveChangesAsync();

        await using var unguarded = _fixture.CreateDbContextWithoutAuditGuard();

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            unguarded.Database.ExecuteSqlRawAsync("TRUNCATE endpoint_platform.audit_log_entries;"));

        exception.MessageText.ShouldContain("append-only");
        exception.MessageText.ShouldContain("TRUNCATE");
    }

    [Fact]
    public async Task Structured_state_snapshots_round_trip_through_jsonb()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(dbContext);

        var entry = AuditLogEntry.For(
                organization.Id,
                DateTimeOffset.UtcNow,
                AuditActorType.PlatformUser,
                Guid.CreateVersion7(),
                "admin@company.local",
                "test.jsonb_round_trip",
                AuditResult.Success)
            .WithStateChange("""{"accountType":"StandardUser"}""", """{"accountType":"Administrator"}""")
            .FromRequest(IPAddress.Parse("10.20.30.40"), "agent/1.0", "corr-jsonb")
            .Build();

        dbContext.AuditLogEntries.Add(entry);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var stored = await dbContext.AuditLogEntries.SingleAsync(a => a.Id == entry.Id);

        stored.NewState.ShouldNotBeNull();
        stored.NewState.ShouldContain("Administrator");
        stored.PreviousState.ShouldNotBeNull();
        stored.PreviousState.ShouldContain("StandardUser");
        stored.SourceIp.ShouldBe(IPAddress.Parse("10.20.30.40"));
    }

    /// <summary>
    /// inet is a real network type, not text. This proves the column supports subnet
    /// containment, which is what makes "show everything from this network" a cheap
    /// query during an investigation.
    /// </summary>
    [Fact]
    public async Task Source_ip_is_queryable_as_a_network_type()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organization = await EnsureOrganizationAsync(dbContext);

        var entry = AuditLogEntry.For(
                organization.Id, DateTimeOffset.UtcNow, AuditActorType.Agent,
                Guid.CreateVersion7(), "PC-023", "test.inet_query", AuditResult.Success)
            .FromRequest(IPAddress.Parse("10.44.1.7"), null, null)
            .Build();

        dbContext.AuditLogEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM endpoint_platform.audit_log_entries WHERE source_ip << inet '10.44.0.0/16';";

        var count = (long)(await command.ExecuteScalarAsync())!;
        count.ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task<Organization> EnsureOrganizationAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext dbContext)
    {
        var existing = await dbContext.Organizations.FirstOrDefaultAsync(o => o.Slug == "test-org");

        if (existing is not null)
        {
            return existing;
        }

        var organization = new Organization("Test Organization", "test-org");
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync();
        return organization;
    }

    private static AuditLogEntry BuildEntry(Guid organizationId, string action) =>
        AuditLogEntry.For(
                organizationId,
                DateTimeOffset.UtcNow,
                AuditActorType.System,
                actorId: null,
                actorDisplay: "integration-test",
                action: action,
                result: AuditResult.Success)
            .Build();
}
