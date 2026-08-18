using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Communication;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Enrollment;
using EndpointAgent.Core.Heartbeat;
using EndpointAgent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace EndpointAgent.Service;

/// <summary>
/// Composition root for the Windows endpoint agent service.
/// </summary>
/// <remarks>
/// Runs as LocalSystem in production and is therefore a privileged security
/// boundary. This file does DI wiring, configuration and logging only: no
/// business logic and no Windows API calls, so the privileged surface stays
/// confined to reviewed, individually tested types in
/// <c>EndpointAgent.Windows</c>.
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

            // ENDPOINTAGENT_Agent__ServerBaseUrl, ENDPOINTAGENT_Enrollment__Token, ...
            builder.Configuration.AddEnvironmentVariables(prefix: "ENDPOINTAGENT_");

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

            builder.Services.AddOptions<EnrollmentOptions>()
                .Bind(builder.Configuration.GetSection(EnrollmentOptions.SectionName));

            builder.Services.AddSingleton(TimeProvider.System);

            // Windows implementations of the platform-neutral abstractions.
            builder.Services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
            builder.Services.AddSingleton<IDeviceCredentialStore, DpapiDeviceCredentialStore>();
            builder.Services.AddSingleton<ILocalAccountsCollector, WindowsLocalAccountsCollector>();
            builder.Services.AddSingleton<ISoftwareCollector, WindowsSoftwareCollector>();
            builder.Services.AddSingleton<ISecurityPostureCollector, WindowsSecurityPostureCollector>();
            builder.Services.AddSingleton<IInventoryCollector, WindowsInventoryCollector>();
            builder.Services.AddSingleton<IDeviceControl, WindowsDeviceControl>();

            // Task pipeline: dispatcher + one executor per supported task type.
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.TaskDispatcher>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.PingTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.RefreshInventoryTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.RestartTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ShutdownTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.LockTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.SignOutTaskExecutor>();

            builder.Services.AddHttpClient<IAgentApiClient, AgentApiClient>((serviceProvider, client) =>
                {
                    var options = serviceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;
                    client.BaseAddress = new Uri(options.ServerBaseUrl, UriKind.Absolute);
                    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
                })
                .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                {
                    var options = serviceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;
                    var handler = new HttpClientHandler();

                    // Only reachable in Debug builds - options validation refuses it
                    // otherwise. Never ships enabled.
                    if (options.AllowUntrustedServerCertificate && IsDebugBuild)
                    {
                        handler.ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }

                    return handler;
                });

            builder.Services.AddSingleton<AgentEnrollmentManager>();
            builder.Services.AddSingleton<HeartbeatLoop>();
            builder.Services.AddHostedService<AgentWorker>();

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
