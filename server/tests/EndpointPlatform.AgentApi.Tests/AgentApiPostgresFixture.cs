using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The full Agent API stack against a real PostgreSQL container: enrollment and
/// heartbeat behaviour is only meaningful with real uniqueness constraints,
/// optimistic concurrency (xmin) and triggers underneath it.
/// </summary>
public sealed class AgentApiPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6-alpine")
        .WithDatabase("endpoint_platform_agentapi_test")
        .WithUsername("test_owner")
        .WithPassword("test_owner_password_not_a_real_secret")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Migrate + seed before the host starts.
        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();

            var seeder = new ReferenceDataSeeder(
                dbContext,
                Options.Create(new SeedOptions()),
                NullLogger<ReferenceDataSeeder>.Instance);
            await seeder.SeedAsync();
        }

        var connectionString = _container.GetConnectionString();

        _factory = new AgentApiTestFactory(connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _container.DisposeAsync();
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

    private sealed class AgentApiTestFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Database:ConnectionString", connectionString);
            // Redis stays unreachable: nothing in enrollment/heartbeat needs it,
            // and the tests must prove that.
            builder.UseSetting("Redis:ConnectionString", "127.0.0.1:1,abortConnect=false,connectTimeout=100");
            builder.UseSetting("Redis:InstanceName", "endpointplatform:agentapitest:");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AgentApiPostgresCollection : ICollectionFixture<AgentApiPostgresFixture>
{
    public const string Name = "agent-api-postgres";
}
