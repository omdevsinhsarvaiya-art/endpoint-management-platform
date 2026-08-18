namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Power and session control for the local machine.
/// </summary>
/// <remarks>
/// The Windows implementation uses Win32 APIs (InitiateSystemShutdownEx,
/// LockWorkStation, WTSDisconnectSession) - never a shell command (ADR-0005).
/// These are destructive operations; they run only in response to an
/// authenticated, permission-checked, audited task delivered by the server.
/// </remarks>
public interface IDeviceControl
{
    Task RestartAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default);

    Task ShutdownAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default);

    Task LockAsync(CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);
}
