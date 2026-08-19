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

    /// <summary>
    /// Everything the agent needs to install an approved package, self-contained so
    /// the install decision never depends on a second lookup. The agent downloads
    /// the package content, verifies it against <paramref name="Sha256"/> and
    /// <paramref name="RequiredSignerSubject"/>, and installs it through the Windows
    /// Installer service only if both pins hold.
    /// </summary>
    /// <param name="PackageId">Package to download from the Agent API.</param>
    /// <param name="Sha256">Lowercase-hex content hash the downloaded bytes must match.</param>
    /// <param name="MsiProductCode">
    /// MSI ProductCode (braced GUID). Backs idempotency (already-installed is a
    /// success) and post-install verification.
    /// </param>
    /// <param name="RequiredSignerSubject">
    /// Substring the Authenticode signer subject must contain, or null to accept any
    /// trusted signature. Never accepts an unsigned file.
    /// </param>
    /// <param name="PackageName">Display name, for logging and the result message.</param>
    /// <param name="Version">Display version.</param>
    public sealed record InstallPackage(
        Guid PackageId,
        string Sha256,
        string MsiProductCode,
        string? RequiredSignerSubject,
        string PackageName,
        string Version);

    // ---- Local Windows account management (Phase 4 write side) ----
    //
    // Targets are identified by SID (the stable key; names are renameable), with the
    // last-known name carried alongside purely for logging/result messages. NO payload
    // ever carries a password: secrets travel by short-lived, one-time reference that
    // the agent redeems out-of-band from the server, so the plaintext never enters the
    // persisted PayloadJson (see docs/adr and the ephemeral-secret store).

    /// <param name="Username">Account (SAM) name to create.</param>
    /// <param name="FullName">Optional display name.</param>
    /// <param name="Description">Optional account description.</param>
    /// <param name="SecretRef">One-time reference the agent redeems to obtain the initial password.</param>
    /// <param name="Enabled">Whether the account is enabled on creation.</param>
    /// <param name="MustChangePasswordAtNextLogon">Force a password change at first logon.</param>
    /// <param name="Administrator">
    /// When true the agent adds the new account to BUILTIN\Administrators and verifies
    /// the membership before reporting success. The server has already checked that the
    /// operator holds the permission to grant it; this flag only carries the decision.
    /// </param>
    /// <param name="AdditionalGroups">
    /// Local groups to join after creation, already validated server-side against the
    /// permitted-groups allow-list. Never contains a protected group.
    /// </param>
    /// <param name="ProfileKey">The baseline this request came from, for the audit record.</param>
    public sealed record CreateLocalUser(
        string Username,
        string? FullName,
        string? Description,
        string SecretRef,
        bool Enabled,
        bool MustChangePasswordAtNextLogon,
        bool Administrator,
        IReadOnlyList<string> AdditionalGroups,
        string ProfileKey);

    /// <summary>Target a single local user by SID (name carried for logging only).</summary>
    public sealed record LocalUserTarget(string Sid, string Username);

    /// <param name="Sid">Target user SID.</param>
    /// <param name="Username">Last-known name (logging).</param>
    /// <param name="Enabled">Desired enabled state.</param>
    public sealed record SetLocalUserEnabled(string Sid, string Username, bool Enabled);

    /// <param name="Sid">Target user SID.</param>
    /// <param name="Username">Last-known name (logging).</param>
    /// <param name="SecretRef">One-time reference the agent redeems to obtain the new password.</param>
    public sealed record ResetLocalUserPassword(string Sid, string Username, string SecretRef);

    /// <param name="Sid">Target user SID.</param>
    /// <param name="Username">Last-known name (logging).</param>
    /// <param name="Administrator">
    /// True = add to BUILTIN\Administrators (promote); false = remove (demote). The agent
    /// changes real Windows group membership; this is never a database-only flag.
    /// </param>
    public sealed record ChangeLocalUserType(string Sid, string Username, bool Administrator);

    /// <param name="GroupSid">Target local group SID (e.g. S-1-5-32-544).</param>
    /// <param name="GroupName">Last-known group name (logging; agent resolves the SID to the localized name).</param>
    /// <param name="MemberSid">SID of the account to add/remove.</param>
    /// <param name="MemberName">Last-known member name (logging).</param>
    public sealed record LocalGroupMembership(
        string GroupSid, string GroupName, string MemberSid, string MemberName);
}
