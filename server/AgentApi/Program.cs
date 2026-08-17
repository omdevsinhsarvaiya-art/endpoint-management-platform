using System.Reflection;
using EndpointPlatform.AgentApi.Endpoints;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Common;
using EndpointPlatform.Infrastructure.DependencyInjection;
using EndpointPlatform.Infrastructure.Hosting;
using Serilog;

namespace EndpointPlatform.AgentApi;

/// <summary>
/// Host for the AGENT API: the trust boundary for enrolled machine identities.
/// </summary>
/// <remarks>
/// <para>
/// Separate process, separate port, separate authentication scheme from the Admin
/// API. An enrolled device credential authenticates here and nowhere else, and no
/// administrator session is accepted here at all.
/// </para>
/// <para>
/// CORS is deliberately not configured. Agents are Windows services making
/// server-to-server calls; a browser has no business reaching this API, and
/// enabling CORS would only widen the attack surface.
/// </para>
/// </remarks>
public sealed class Program
{
    // Never instantiated. Non-static only so WebApplicationFactory<TEntryPoint>
    // can use it to locate the entry-point assembly for integration tests.
    private Program()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration.AddEnvironmentVariables(prefix: "ENDPOINTPLATFORM_");

            builder.Host.UsePlatformSerilog();

            builder.Services.AddPlatformHosting();
            builder.Services.AddEndpointPlatformInfrastructure(builder.Configuration, builder.Environment);

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new()
                {
                    Title = "Endpoint Platform Agent API",
                    Version = "v1",
                    Description = "Machine-to-machine API for enrolled Windows agents. "
                                  + "Not for administrator or browser use.",
                });
            });

            var app = builder.Build();

            app.UsePlatformRequestPipeline();

            app.UseExceptionHandler();
            app.UseStatusCodePages();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            else
            {
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            app.MapPlatformHealthChecks();

            app.MapAgentEndpoints();

            app.MapGet("/", () => new ServiceInfoResponse(
                    Service: "agent-api",
                    Version: BuildVersion,
                    Environment: app.Environment.EnvironmentName))
                .WithName("GetAgentApiServiceInfo")
                .AllowAnonymous();

            Log.Information(
                "Agent API starting. Environment: {Environment}, version: {Version}, "
                + "agent protocol: {ProtocolVersion}.",
                app.Environment.EnvironmentName,
                BuildVersion,
                AgentProtocol.Version);

            await app.RunAsync();
            return 0;
        }
        // See the identical filter in the Admin API host: HostAbortedException is
        // test/tooling control flow and must propagate.
        catch (Exception ex) when (ex is not HostAbortedException
                                   && ex.GetType().Name != "StopTheHostException")
        {
            Log.Fatal(ex, "Agent API terminated unexpectedly during startup.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    internal static string BuildVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
