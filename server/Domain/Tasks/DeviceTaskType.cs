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

    /// <summary>
    /// Update the agent itself to an approved published release (Milestone 10).
    /// Deliberately NOT an InstallPackage: the package path installs in-process,
    /// and an in-process install of the agent's own MSI would be killed when the
    /// upgrade stops the very service running it. This type routes to an
    /// executor that verifies everything first and then hands the install to
    /// Windows in a way that survives the agent's own shutdown.
    /// </summary>
    UpdateAgent = 31,

    // Local Windows account management (Phase 4 write side). Each acts on a real
    // local user/group through account-management APIs on the agent - never a shell
    // (ADR-0005). Targets are identified by SID (names are renameable).

    /// <summary>Create a local Windows user. Password delivered out-of-band by secret reference.</summary>
    CreateLocalUser = 40,

    /// <summary>Delete a local Windows user (by SID).</summary>
    DeleteLocalUser = 41,

    /// <summary>Enable a local Windows user (clear the account-disabled flag).</summary>
    EnableLocalUser = 42,

    /// <summary>Disable a local Windows user (set the account-disabled flag).</summary>
    DisableLocalUser = 43,

    /// <summary>Reset a local user's password. Secret delivered out-of-band by reference.</summary>
    ResetLocalUserPassword = 44,

    /// <summary>Force a local user to change their password at next logon.</summary>
    ForceLocalUserPasswordChange = 45,

    /// <summary>Promote/demote a local user by adding/removing BUILTIN\Administrators membership.</summary>
    ChangeLocalUserType = 46,

    /// <summary>Add a local user (by SID) to a local group (by SID).</summary>
    AddLocalUserToGroup = 47,

    /// <summary>Remove a local user (by SID) from a local group (by SID).</summary>
    RemoveLocalUserFromGroup = 48,
}
