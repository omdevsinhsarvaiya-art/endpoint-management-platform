using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace EndpointPlatform.Migrations;

/// <summary>
/// Builds a <see cref="EndpointPlatformDbContext"/> for the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// <para>
/// Used only at design time, when generating or scripting a migration. It never
/// runs in a deployed process.
/// </para>
/// <para>
/// The connection string is read from configuration and falls back to a
/// placeholder, because <c>dotnet ef migrations add</c> only needs a syntactically
/// valid string to build the model - it does not connect. Commands that DO connect
/// (<c>database update</c>) require the real value to be supplied, which is why the
/// fallback points at an obviously non-production database name rather than a
/// plausible one.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EndpointPlatformDbContext>
{
    private const string DesignTimeFallbackConnectionString =
        "Host=localhost;Port=55432;Database=endpoint_platform_designtime;Username=postgres;Password=postgres";

    public EndpointPlatformDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix: "ENDPOINTPLATFORM_")
            .AddEnvironmentVariables()
            .Build();

        var connectionString =
            configuration["Database:ConnectionString"]
            ?? configuration["ConnectionStrings:EndpointPlatform"]
            ?? DesignTimeFallbackConnectionString;

        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            .Options;

        return new EndpointPlatformDbContext(options);
    }
}
