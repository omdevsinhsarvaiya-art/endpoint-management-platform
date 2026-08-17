using System.Reflection;
using EndpointPlatform.Api.Endpoints;
using EndpointPlatform.Contracts.Common;
using EndpointPlatform.Infrastructure.DependencyInjection;
using EndpointPlatform.Infrastructure.Hosting;
using Serilog;

namespace EndpointPlatform.Api;

/// <summary>
/// Host for the ADMIN API: the trust boundary for authenticated human administrators.
/// </summary>
/// <remarks>
/// This host never exposes agent endpoints. Agents talk to
/// <c>EndpointPlatform.AgentApi</c>, which runs as a separate process on a separate
/// port with a separate authentication scheme. Keeping them apart means a flaw in
/// agent request handling cannot reach an administrator endpoint, and a stolen
/// device credential is useless here. See docs/adr/0001-separate-admin-and-agent-apis.md
/// </remarks>
public sealed class Program
{
    // Never instantiated. The class is non-static only because
    // WebApplicationFactory<TEntryPoint> requires a reference type argument to
    // locate the entry-point assembly for in-process integration tests.
    private Program()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        // Bootstrap logger: captures failures that happen before configuration has
        // been read, which would otherwise be lost entirely.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            // ENDPOINTPLATFORM_Database__ConnectionString etc. Secrets come from the
            // environment or user-secrets, never from a committed settings file.
            builder.Configuration.AddEnvironmentVariables(prefix: "ENDPOINTPLATFORM_");

            builder.Host.UsePlatformSerilog();

            builder.Services.AddPlatformHosting();
            builder.Services.AddEndpointPlatformInfrastructure(builder.Configuration, builder.Environment);

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new()
                {
                    Title = "Endpoint Platform Admin API",
                    Version = "v1",
                    Description = "Administrative API for the Endpoint Management Platform. "
                                  + "Requires an authenticated administrator; not for agent use.",
                });
            });

            builder.Services.AddCors(options => options.AddPolicy(
                DashboardCorsPolicy,
                policy => policy
                    .WithOrigins(ReadDashboardOrigins(builder.Configuration))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .WithExposedHeaders(CorrelationId.HeaderName)));

            var app = builder.Build();

            app.UsePlatformRequestPipeline();

            // Returns RFC 7807 problem details instead of a stack trace.
            app.UseExceptionHandler();
            app.UseStatusCodePages();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            else
            {
                // Only outside Development: HSTS on localhost poisons the browser
                // cache for every other local project.
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            app.UseCors(DashboardCorsPolicy);

            app.MapPlatformHealthChecks();

            app.MapEnrollmentTokenEndpoints();
            app.MapDeviceEndpoints();

            app.MapGet("/", () => new ServiceInfoResponse(
                    Service: "admin-api",
                    Version: BuildVersion,
                    Environment: app.Environment.EnvironmentName))
                .WithName("GetAdminApiServiceInfo")
                .AllowAnonymous();

            Log.Information(
                "Admin API starting. Environment: {Environment}, version: {Version}.",
                app.Environment.EnvironmentName,
                BuildVersion);

            await app.RunAsync();
            return 0;
        }
        // HostAbortedException (and its pre-.NET-7 internal equivalent) is control
        // flow, not failure: WebApplicationFactory and the EF design-time tooling
        // abort the entry point this way after capturing the configured host.
        // Swallowing it would break in-process integration testing.
        catch (Exception ex) when (ex is not HostAbortedException
                                   && ex.GetType().Name != "StopTheHostException")
        {
            Log.Fatal(ex, "Admin API terminated unexpectedly during startup.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    internal const string DashboardCorsPolicy = "dashboard";

    internal static string BuildVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// Reads the dashboard origins allowed to call this API.
    /// </summary>
    /// <remarks>
    /// An explicit allow-list, never <c>AllowAnyOrigin</c>. The Admin API is
    /// credentialed, and a wildcard origin combined with credentials is exactly the
    /// configuration that lets any website drive an administrator's session.
    /// </remarks>
    private static string[] ReadDashboardOrigins(IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        if (origins is null or { Length: 0 })
        {
            throw new InvalidOperationException(
                "Cors:AllowedOrigins is not configured. The Admin API refuses to start without an "
                + "explicit dashboard origin allow-list. See docs/development.md.");
        }

        return origins;
    }
}
