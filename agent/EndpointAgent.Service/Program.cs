using System.Runtime.Versioning;
using EndpointAgent.Core;
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
using Serilog.Events;

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
    /// <summary>
    /// Whether the installer registered our Event Log source. Probing avoids the
    /// sink throwing on a machine where the agent was started without installing
    /// (a developer run), which would take the whole logging pipeline down.
    /// </summary>
    private static bool EventLogSourceExists()
    {
        try
        {
            return System.Diagnostics.EventLog.SourceExists("EndpointPlatformAgent");
        }
        catch (Exception)
        {
            // Reading the source registry requires privileges a non-elevated
            // developer run may not have. Absence of proof, so: skip the sink.
            return false;
        }
    }

    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Machine-wide configuration, written by the installer into the
            // ACL-protected state directory. It is read AFTER appsettings.json so an
            // installed agent overrides the build-time defaults, and BEFORE
            // environment variables so a developer can still override locally
            // without editing a protected file.
            //
            // Program Files is deliberately not the config location: the service
            // account can write state but must not be able to rewrite its own
            // binaries' directory.
            builder.Configuration.AddJsonFile(
                Path.Combine(AgentPaths.StateDirectory, "agent.config.json"),
                optional: true,
                reloadOnChange: false);

            // ENDPOINTAGENT_Agent__ServerBaseUrl, ENDPOINTAGENT_Enrollment__Token, ...
            builder.Configuration.AddEnvironmentVariables(prefix: "ENDPOINTAGENT_");

            builder.Services.AddWindowsService(options => options.ServiceName = "EndpointPlatformAgent");

            builder.Services.AddSerilog((services, configuration) =>
            {
                configuration
                    .ReadFrom.Configuration(builder.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Enrich.WithMachineName();

                // A Windows Service has no console, so the console sink configured in
                // appsettings.json reaches nobody once this runs under the SCM. These
                // two are the ones that actually matter in production, and they are
                // configured here rather than in JSON so the paths come from
                // AgentPaths instead of being duplicated as escaped strings.
                configuration.WriteTo.File(
                    Path.Combine(AgentPaths.LogDirectory, "agent-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 20 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

                // Warnings and errors also go where an administrator already looks.
                // The event source is created by the installer, which runs elevated;
                // creating it here would fail for a service that is not an admin.
                if (OperatingSystem.IsWindows() && EventLogSourceExists())
                {
                    configuration.WriteTo.EventLog(
                        source: "EndpointPlatformAgent",
                        logName: "Application",
                        manageEventSource: false,
                        restrictedToMinimumLevel: LogEventLevel.Warning);
                }
            });

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

            // Survives service restart and reboot while a request waits for approval,
            // so the agent resumes its request instead of orphaning one.
            builder.Services.AddSingleton<IEnrollmentStateStore, DpapiEnrollmentStateStore>();
            builder.Services.AddSingleton<ILocalAccountsCollector, WindowsLocalAccountsCollector>();
            builder.Services.AddSingleton<ISoftwareCollector, WindowsSoftwareCollector>();
            builder.Services.AddSingleton<ISecurityPostureCollector, WindowsSecurityPostureCollector>();
            builder.Services.AddSingleton<IWindowsUpdateCollector, WindowsUpdateCollector>();
            builder.Services.AddSingleton<WindowsServiceProcessProvider>();
            builder.Services.AddSingleton<IServiceProcessCollector>(sp => sp.GetRequiredService<WindowsServiceProcessProvider>());
            builder.Services.AddSingleton<IServiceProcessControl>(sp => sp.GetRequiredService<WindowsServiceProcessProvider>());
            builder.Services.AddSingleton<IScreenLockPolicyReader, WindowsScreenLockPolicyReader>();
            builder.Services.AddSingleton<EndpointAgent.Core.Policies.PolicyEvaluator>();
            builder.Services.AddSingleton<EndpointAgent.Core.Policies.PolicyRunner>();
            builder.Services.AddSingleton<IInventoryCollector, WindowsInventoryCollector>();
            builder.Services.AddSingleton<IDeviceControl, WindowsDeviceControl>();
            builder.Services.AddSingleton<IPackageInstaller, WindowsMsiPackageInstaller>();
            builder.Services.AddSingleton<ILocalAccountsControl, WindowsLocalAccountControl>();
            builder.Services.AddSingleton<ISecretRedeemer, EndpointAgent.Core.Communication.ServerSecretRedeemer>();

            // Task pipeline: dispatcher + one executor per supported task type.
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.TaskDispatcher>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.PingTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.RefreshInventoryTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.RestartTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ShutdownTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.LockTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.SignOutTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ControlServiceTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.TerminateProcessTaskExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.InstallPackageExecutor>();

            // USB and peripheral control (Milestone 11). The enforcer is the only
            // component here that changes machine state, and the widest state it
            // can express is read-only.
            builder.Services.AddSingleton<IUsbDeviceEnumerator, WindowsUsbDeviceEnumerator>();
            builder.Services.AddSingleton<IUsbPolicyEnforcer, WindowsUsbPolicyEnforcer>();
            builder.Services.AddSingleton<IUsbDeviceWatcher, WindowsUsbDeviceWatcher>();
            builder.Services.AddSingleton<IUsbGrantStore, DpapiUsbGrantStore>();
            builder.Services.AddSingleton<IUsbRestrictionLedger, FileUsbRestrictionLedger>();
            builder.Services.AddSingleton<EndpointAgent.Core.Usb.UsbPolicyManager>();
            builder.Services.AddSingleton<EndpointAgent.Core.Usb.UsbMonitorLoop>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ApplyUsbPolicyExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Abstractions.IAgentUpdateLauncher, EndpointAgent.Windows.WindowsAgentUpdateLauncher>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.UpdateAgentExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.CreateLocalUserExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.DeleteLocalUserExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.EnableLocalUserExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.DisableLocalUserExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ResetLocalUserPasswordExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ForceLocalUserPasswordChangeExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.ChangeLocalUserTypeExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.AddLocalUserToGroupExecutor>();
            builder.Services.AddSingleton<EndpointAgent.Core.Tasks.ITaskExecutor, EndpointAgent.Core.Tasks.RemoveLocalUserFromGroupExecutor>();

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

            // Before anything touches enrollment or credentials: if an update's
            // installer removed the live state files, put identity back from the
            // pre-update snapshot so this start is a reconnect, not a re-enrol.
            EndpointAgent.Core.AgentStateRestore.RestoreIfNeeded(
                host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger("EndpointAgent.StateRestore"));

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
