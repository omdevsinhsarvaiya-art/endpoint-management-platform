using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Service;
using EndpointAgent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace EndpointAgent.Service;

/// <summary>
/// Composition root for the Windows endpoint agent service.
/// </summary>
/// <remarks>
/// <para>
/// Runs as LocalSystem in production and is therefore a privileged security
/// boundary, not an ordinary desktop application. This file does DI wiring,
/// configuration and logging only: no business logic and no Windows API calls, so
/// the privileged surface stays confined to reviewed, individually tested types in
/// <c>EndpointAgent.Windows</c>.
/// </para>
/// <para>
/// Phase 0 scope: the service starts, binds configuration, resolves its Windows
/// dependencies and logs what it can see about the machine. It performs no
/// enrollment, holds no credential and sends nothing to the server - that arrives
/// in Phase 1.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Configuration.AddEnvironmentVariables(prefix: "ENDPOINTAGENT_");

            // Lets the same executable run as a Windows service and as a console
            // application for debugging, without a separate build.
            builder.Services.AddWindowsService(options => options.ServiceName = "EndpointPlatformAgent");

            builder.Services.AddSerilog((services, configuration) => configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName());

            builder.Services.AddOptions<AgentOptions>()
                .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
                .ValidateDataAnnotations()
                .Validate(
                    static options => !options.AllowUntrustedServerCertificate || IsDebugBuild,
                    "Agent:AllowUntrustedServerCertificate may only be enabled in a Debug build. "
                    + "An agent that does not validate the server certificate can be man-in-the-middled "
                    + "into accepting hostile privileged tasks.")
                .ValidateOnStart();

            builder.Services.AddSingleton(TimeProvider.System);

            // Windows implementations of the platform-neutral abstractions.
            builder.Services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();

            builder.Services.AddHostedService<AgentStartupDiagnosticsService>();

            var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Endpoint agent terminated unexpectedly during startup.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
