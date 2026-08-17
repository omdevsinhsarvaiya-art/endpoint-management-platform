using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// Verifies that seeding brings the database into line with the code catalogue and
/// stays there across repeated runs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReferenceDataSeederTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static readonly SeedOptions Options = new()
    {
        DefaultOrganizationName = "Seeder Test Org",
        DefaultOrganizationSlug = "seeder-test",
    };

    private ReferenceDataSeeder CreateSeeder(Infrastructure.Persistence.EndpointPlatformDbContext dbContext) =>
        new(dbContext, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<ReferenceDataSeeder>.Instance);

    [Fact]
    public async Task Seeding_creates_every_permission_in_the_catalogue()
    {
        await using var dbContext = _fixture.CreateDbContext();
        await CreateSeeder(dbContext).SeedAsync();

        var storedKeys = await dbContext.Permissions.Select(p => p.Key).ToListAsync();

        foreach (var definition in Permissions.All)
        {
            storedKeys.ShouldContain(definition.Key);
        }
    }

    [Fact]
    public async Task Seeding_creates_the_four_built_in_roles()
    {
        await using var dbContext = _fixture.CreateDbContext();
        await CreateSeeder(dbContext).SeedAsync();

        var roleKeys = await dbContext.Roles.Where(r => r.IsBuiltIn).Select(r => r.Key).ToListAsync();

        roleKeys.ShouldContain(SystemRoles.SuperAdministrator);
        roleKeys.ShouldContain(SystemRoles.ItAdministrator);
        roleKeys.ShouldContain(SystemRoles.Helpdesk);
        roleKeys.ShouldContain(SystemRoles.Auditor);
    }

    [Fact]
    public async Task Built_in_role_grants_match_the_code_definition_exactly()
    {
        await using var dbContext = _fixture.CreateDbContext();
        await CreateSeeder(dbContext).SeedAsync();
        dbContext.ChangeTracker.Clear();

        var permissionKeyById = await dbContext.Permissions.ToDictionaryAsync(p => p.Id, p => p.Key);

        foreach (var (roleKey, definition) in SystemRoles.All)
        {
            var role = await dbContext.Roles
                .Include(r => r.Permissions)
                .SingleAsync(r => r.IsBuiltIn && r.Key == roleKey);

            var actual = role.Permissions.Select(p => permissionKeyById[p.PermissionId]).ToHashSet();

            actual.ShouldBe(definition.PermissionKeys.ToHashSet(), ignoreOrder: true);
        }
    }

    /// <summary>
    /// Seeding runs on every deployment, so a second run must be a no-op. If it were
    /// not, every restart would churn role grants and produce spurious changes.
    /// </summary>
    [Fact]
    public async Task Seeding_is_idempotent()
    {
        await using var first = _fixture.CreateDbContext();
        await CreateSeeder(first).SeedAsync();

        await using var second = _fixture.CreateDbContext();
        var result = await CreateSeeder(second).SeedAsync();

        result.PermissionChanges.ShouldBe(0);
        result.RoleGrantChanges.ShouldBe(0);
        result.DefaultOrganizationCreated.ShouldBeFalse();
    }

    /// <summary>
    /// The critical reconciliation property: if someone grants a built-in role an
    /// extra permission directly in the database, the next seeding run must take it
    /// away. Otherwise a privilege escalation performed via SQL would survive
    /// indefinitely.
    /// </summary>
    [Fact]
    public async Task Seeding_revokes_a_permission_added_to_a_built_in_role_out_of_band()
    {
        await using var setup = _fixture.CreateDbContext();
        await CreateSeeder(setup).SeedAsync();
        setup.ChangeTracker.Clear();

        var auditor = await setup.Roles.Include(r => r.Permissions)
            .SingleAsync(r => r.IsBuiltIn && r.Key == SystemRoles.Auditor);

        var shutdownPermission = await setup.Permissions.SingleAsync(p => p.Key == Permissions.Device.Shutdown);

        auditor.GrantPermission(shutdownPermission.Id);
        await setup.SaveChangesAsync();
        setup.ChangeTracker.Clear();

        // Confirm the out-of-band grant really landed.
        var tampered = await setup.Roles.Include(r => r.Permissions)
            .SingleAsync(r => r.IsBuiltIn && r.Key == SystemRoles.Auditor);
        tampered.Permissions.Any(p => p.PermissionId == shutdownPermission.Id).ShouldBeTrue();

        await using var reseed = _fixture.CreateDbContext();
        var result = await CreateSeeder(reseed).SeedAsync();
        result.RoleGrantChanges.ShouldBeGreaterThan(0);

        await using var verify = _fixture.CreateDbContext();
        var reconciled = await verify.Roles.Include(r => r.Permissions)
            .SingleAsync(r => r.IsBuiltIn && r.Key == SystemRoles.Auditor);

        reconciled.Permissions.Any(p => p.PermissionId == shutdownPermission.Id).ShouldBeFalse(
            "seeding must revoke a permission that was granted to a built-in role out of band");
    }

    [Fact]
    public async Task Seeding_creates_no_account_that_can_sign_in()
    {
        // A bootstrap administrator would need a credential, and a credential in
        // source or in an image is a hardcoded secret. Bootstrap is an explicit
        // operator step introduced with authentication in Phase 3.
        await using var dbContext = _fixture.CreateDbContext();
        await CreateSeeder(dbContext).SeedAsync();

        var usersWithCredentials = await dbContext.PlatformUsers
            .Where(u => u.PasswordHash != null)
            .CountAsync();

        usersWithCredentials.ShouldBe(0);
    }
}
