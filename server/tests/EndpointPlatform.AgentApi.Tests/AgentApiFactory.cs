using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>Boots the real Agent API host in-process. See AdminApiFactory for the rationale.</summary>
public sealed class AgentApiFactory : WebApplicationFactory<Program>
{
    private const string UnreachablePostgres =
        "Host=127.0.0.1;Port=1;Database=unreachable_by_design;Username=none;Password=none";

    private const string UnreachableRedis = "127.0.0.1:1,abortConnect=false,connectTimeout=100";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration - see AdminApiFactory.
        builder.UseSetting("Database:ConnectionString", UnreachablePostgres);
        builder.UseSetting("Redis:ConnectionString", UnreachableRedis);
        builder.UseSetting("Redis:InstanceName", "endpointplatform:agenttest:");
        builder.UseSetting("Redis:AbortOnConnectFail", "false");
    }
}
