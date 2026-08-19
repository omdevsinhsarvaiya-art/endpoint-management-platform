using System.Net.Http.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The full Admin API stack on real PostgreSQL, seeded with one account per
/// built-in role. This is the substrate for the RBAC enforcement matrix.
/// </summary>
public sealed class AdminApiPostgresFixture : IAsyncLifetime
{
    public const string Password = "correct horse battery staple 9!";

    public const string SuperAdminEmail = "superadmin@test.local";
    public const string ItAdminEmail = "itadmin@test.local";
    public const string HelpdeskEmail = "helpdesk@test.local";
    public const string AuditorEmail = "auditor@test.local";
    public const string DisabledEmail = "disabled@test.local";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6-alpine")
        .WithDatabase("endpoint_platform_adminapi_test")
        .WithUsername("test_owner")
        .WithPassword("test_owner_password_not_a_real_secret")
        .Build();

    // A real Redis so the ephemeral-secret path (create user / reset password) is
    // exercised end to end rather than short-circuited by an unreachable cache.
    private readonly RedisContainer _redis = new RedisBuilder().Build();

    private WebApplicationFactory<Program>? _factory;

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await _redis.StartAsync();

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();

            var seeder = new ReferenceDataSeeder(
                dbContext, Options.Create(new SeedOptions()), NullLogger<ReferenceDataSeeder>.Instance);
            await seeder.SeedAsync();

            var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var roles = await dbContext.Roles.Where(r => r.IsBuiltIn).ToDictionaryAsync(r => r.Key);

            AddUser(dbContext, organization.Id, SuperAdminEmail, roles[SystemRoles.SuperAdministrator].Id);
            AddUser(dbContext, organization.Id, ItAdminEmail, roles[SystemRoles.ItAdministrator].Id);
            AddUser(dbContext, organization.Id, HelpdeskEmail, roles[SystemRoles.Helpdesk].Id);
            AddUser(dbContext, organization.Id, AuditorEmail, roles[SystemRoles.Auditor].Id);

            var disabled = AddUser(dbContext, organization.Id, DisabledEmail, roles[SystemRoles.ItAdministrator].Id);
            disabled.Disable();

            await dbContext.SaveChangesAsync();
        }

        var connectionString = _container.GetConnectionString();
        _factory = new AdminApiTestFactory(connectionString, _redis.GetConnectionString());
    }

    private static PlatformUser AddUser(
        EndpointPlatformDbContext dbContext, Guid organizationId, string email, Guid roleId)
    {
        var user = new PlatformUser(organizationId, email, email.Split('@')[0]);
        user.SetPasswordHash(PasswordHasher.Hash(Password), DateTimeOffset.UtcNow);
        user.AssignRole(roleId);

        // These fixture accounts stand in for established operators, which the
        // production migration grants organization-wide scope. Tests that exercise
        // deny-by-default create their own unscoped administrator instead.
        user.GrantAllDeviceScope();
        dbContext.PlatformUsers.Add(user);
        return user;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _container.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public EndpointPlatformDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            .AddInterceptors(
                new AuditableEntityInterceptor(TimeProvider.System),
                new AuditImmutabilityInterceptor())
            .Options;

        return new EndpointPlatformDbContext(options);
    }

    /// <summary>Signs the account in through the real endpoint and returns a Bearer token.</summary>
    public async Task<string> SignInAsync(string email, string password = Password)
    {
        using var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email, password });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("sessionToken").GetString()!;
    }

    public HttpClient CreateClientFor(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private sealed class AdminApiTestFactory(string connectionString, string redisConnectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Redis:ConnectionString", redisConnectionString);
            builder.UseSetting("Redis:InstanceName", "endpointplatform:adminapitest:");
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            // Every test request arrives from the same loopback address, so the
            // per-address login limiter must be generous here. The limiter itself
            // has a dedicated test that lowers this again.
            builder.UseSetting("AdminAuth:LoginAttemptsPerMinutePerAddress", "1000");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AdminApiPostgresCollection : ICollectionFixture<AdminApiPostgresFixture>
{
    public const string Name = "admin-api-postgres";
}
