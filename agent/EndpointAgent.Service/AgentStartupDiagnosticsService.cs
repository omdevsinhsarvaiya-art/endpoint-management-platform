using System.Runtime.Versioning;
using System.Security.Principal;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Service;

/// <summary>
/// Phase 0 placeholder hosted service: proves the host starts, configuration binds
/// and the Windows facilities resolve.
/// </summary>
/// <remarks>
/// Read-only and outbound-silent by design. It contacts no server, stores no
/// credential and changes nothing on the endpoint. The enrollment and heartbeat
/// loops replace it in Phase 1.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AgentStartupDiagnosticsService(
    ISystemInfoProvider systemInfoProvider,
    IOptions<AgentOptions> options,
    ILogger<AgentStartupDiagnosticsService> logger) : BackgroundService
{
    private readonly ISystemInfoProvider _systemInfoProvider = systemInfoProvider
        ?? throw new ArgumentNullException(nameof(systemInfoProvider));

    private readonly AgentOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<AgentStartupDiagnosticsService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostName = _systemInfoProvider.GetHostName();
        var osDescription = await _systemInfoProvider.GetOperatingSystemDescriptionAsync(stoppingToken);

        // The machine identifier is not a secret, but it is a stable machine
        // fingerprint. Log only a short prefix so a shipped log cannot be used to
        // enumerate the estate.
        var machineIdentifier = await _systemInfoProvider.GetMachineIdentifierAsync(stoppingToken);
        var identifierPrefix = machineIdentifier.Length > 8
            ? machineIdentifier[..8] + "..."
            : machineIdentifier;

        _logger.LogInformation(
            "Endpoint agent started. Host: {HostName}, OS: {OperatingSystem}, "
            + "machine id prefix: {MachineIdPrefix}, elevated: {IsElevated}, server: {ServerBaseUrl}.",
            hostName,
            osDescription,
            identifierPrefix,
            IsElevated(),
            // A base URL is not a credential, so logging it aids diagnosis safely.
            string.IsNullOrWhiteSpace(_options.ServerBaseUrl) ? "(not configured)" : _options.ServerBaseUrl);

        _logger.LogInformation(
            "Phase 0 agent: no enrollment, no heartbeat, no credential storage and no outbound "
            + "requests are performed. Idling until shutdown.");

        // Wait for shutdown without spinning. Task.Delay(Infinite) with the stopping
        // token is the documented way to park a BackgroundService that has no loop.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Endpoint agent stopping.");
        }
    }

    /// <summary>
    /// Whether the process holds the local Administrators group in its token.
    /// Logged at startup because almost every later capability depends on it, and
    /// silently running unprivileged is a confusing failure mode.
    /// </summary>
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
