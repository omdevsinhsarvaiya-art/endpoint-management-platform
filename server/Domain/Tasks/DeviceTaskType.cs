namespace EndpointPlatform.Domain.Tasks;

/// <summary>
/// The closed set of task types the platform can queue for an endpoint.
/// </summary>
/// <remarks>
/// <para>
/// This enum is the whole point of the task system: there is no
/// "run arbitrary command" member and never will be (spec rule 17, ADR-0010).
/// Every type maps to a specific, reviewed agent-side executor with a typed,
/// validated payload. Adding remote capability means adding a member here plus
/// its executor plus its tests - a deliberate, reviewable act.
/// </para>
/// <para>
/// Stored as text in PostgreSQL so reordering can never reinterpret history.
/// </para>
/// </remarks>
public enum DeviceTaskType
{
    /// <summary>Benign no-op used to prove the task pipeline end to end. Executor just echoes.</summary>
    Ping = 0,

    /// <summary>Ask the agent to collect and upload a fresh inventory now.</summary>
    RefreshInventory = 1,

    /// <summary>Restart the machine (with a grace period).</summary>
    RestartDevice = 2,

    /// <summary>Shut the machine down (with a grace period).</summary>
    ShutdownDevice = 3,

    /// <summary>Lock the interactive session.</summary>
    LockDevice = 4,

    /// <summary>Sign out the interactive user.</summary>
    SignOutUser = 5,

    /// <summary>Start/stop/restart a named Windows service (Phase 9).</summary>
    ControlService = 20,

    /// <summary>Terminate a process by PID with an expected-image guard (Phase 9).</summary>
    TerminateProcess = 21,

    /// <summary>Install an approved, hash-verified package (Phase 11).</summary>
    InstallPackage = 30,
}
