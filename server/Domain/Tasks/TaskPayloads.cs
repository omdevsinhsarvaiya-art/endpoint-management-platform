namespace EndpointPlatform.Domain.Tasks;

/// <summary>
/// Typed payloads for task types that carry parameters. Serialised to the task's
/// <c>PayloadJson</c>. Payload-free types (Ping, Lock, RefreshInventory) have none.
/// </summary>
public static class TaskPayloads
{
    /// <param name="GraceSeconds">Delay before the action, giving the user warning time.</param>
    /// <param name="Message">Optional message shown to the interactive user.</param>
    public sealed record RestartOrShutdown(int GraceSeconds, string? Message);

    public enum ServiceAction
    {
        Start = 0,
        Stop = 1,
        Restart = 2,
    }

    /// <param name="ServiceName">Windows service short name (validated against a safe pattern).</param>
    public sealed record ControlService(string ServiceName, ServiceAction Action);

    /// <param name="ProcessId">PID to terminate.</param>
    /// <param name="ExpectedImageName">
    /// Executable name the PID must currently have (e.g. <c>notepad.exe</c>). Guards
    /// against a PID being reused by the OS between listing and termination.
    /// </param>
    public sealed record TerminateProcess(int ProcessId, string ExpectedImageName);
}
