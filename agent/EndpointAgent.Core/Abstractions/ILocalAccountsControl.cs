namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Mutates Windows local accounts and group memberships on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate interface from <see cref="ILocalAccountsCollector"/>:
/// reads and writes never share a contract, so code granted "list the users" can
/// never accidentally be handed "delete a user". This mirrors the existing
/// <c>IServiceProcessCollector</c> / <c>IServiceProcessControl</c> split.
/// </para>
/// <para>
/// Targets are identified by SID rather than name. A local account can be renamed;
/// its SID cannot, so a task queued minutes ago cannot land on the wrong account
/// because someone renamed one in between.
/// </para>
/// <para>
/// The Windows implementation must use account-management APIs (netapi32 Net* and
/// the local security APIs) — never a shell, a command line or a spawned process
/// (ADR-0005). Failures surface as thrown exceptions carrying the native status,
/// which the dispatcher converts into a reported task failure.
/// </para>
/// </remarks>
public interface ILocalAccountsControl
{
    /// <summary>
    /// Creates a local user and brings it to the requested end state, atomically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creation is more than one Windows call: the account is added, then named,
    /// then optionally flagged must-change, joined to groups, and promoted. If any
    /// step after the account exists fails, the implementation must remove the
    /// account again so a reported failure means "nothing changed on this machine".
    /// A half-built account left behind by a failed task is exactly the divergence
    /// between reported and real state this design exists to prevent.
    /// </para>
    /// <para>
    /// The returned value is read back FROM Windows after the work, not assembled
    /// from the inputs, so a caller cannot report success for state that was never
    /// achieved.
    /// </para>
    /// <para>
    /// Every account created here joins <c>BUILTIN\Users</c>, whatever its type.
    /// <c>NetUserAdd</c> does not do this on its own, and an account with no local
    /// group membership still authenticates (BUILTIN\Users contains
    /// NT AUTHORITY\Authenticated Users), so the omission does not announce itself -
    /// it has to be established and then verified.
    /// </para>
    /// <para>
    /// <paramref name="additionalGroups"/> are optional: a group this machine does not
    /// have is skipped and named in <see cref="CreatedLocalAccount.SkippedGroups"/>,
    /// not treated as a failure. Which non-essential groups exist varies by Windows
    /// SKU, and destroying an otherwise-correct account over a missing "Remote Desktop
    /// Users" serves nobody. Skipping is reported, never silent — the caller must be
    /// able to see that it did not get everything it asked for.
    /// <paramref name="administrator"/> is NOT optional: it is verified after the fact
    /// and failure to achieve it rolls the account back.
    /// </para>
    /// </remarks>
    ValueTask<CreatedLocalAccount> CreateUserAsync(
        string username,
        string password,
        string? fullName,
        string? description,
        bool enabled,
        bool mustChangePasswordAtNextLogon,
        bool administrator,
        IReadOnlyList<string> additionalGroups,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the local user with this SID.</summary>
    ValueTask DeleteUserAsync(string sid, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables the local user with this SID.</summary>
    ValueTask SetUserEnabledAsync(string sid, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Sets a new password for the local user with this SID.</summary>
    ValueTask SetPasswordAsync(string sid, string password, CancellationToken cancellationToken = default);

    /// <summary>Requires the user to change their password at next logon.</summary>
    ValueTask ForcePasswordChangeAsync(string sid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes the account from a local group, both identified by SID.
    /// Adding an existing member (or removing a non-member) succeeds quietly, so a
    /// retried task converges rather than failing.
    /// </summary>
    ValueTask SetGroupMembershipAsync(
        string groupSid, string memberSid, bool isMember, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live view of the machine's local accounts, used to re-check the safety rules
    /// against real Windows state immediately before a destructive change. Server-side
    /// inventory can be stale; this cannot be.
    /// </summary>
    ValueTask<IReadOnlyList<LiveLocalAccount>> GetLiveAccountsAsync(CancellationToken cancellationToken = default);
}

/// <summary>A local account as it exists on the machine right now.</summary>
public sealed record LiveLocalAccount(string Sid, string Username, bool Enabled, bool IsAdministrator);

/// <summary>
/// The account as Windows reports it immediately after creation. Read back from the
/// OS, never echoed from the request, so it can be trusted as the achieved state.
/// </summary>
/// <param name="Groups">Every group Windows reports the account in, after creation.</param>
/// <param name="SkippedGroups">
/// Requested optional groups this machine does not have. Empty on a fully-applied
/// request. Surfaced so "created" never quietly means "created, minus some of what
/// you asked for".
/// </param>
/// <param name="IsInUsersGroup">
/// Whether Windows reports the account in <c>BUILTIN\Users</c>. Determined by
/// well-known SID rather than by reading <paramref name="Groups"/>, whose names are
/// localized. A created account that is not in this group is treated as a failed
/// create.
/// </param>
public sealed record CreatedLocalAccount(
    string Sid,
    string Username,
    bool Enabled,
    bool IsAdministrator,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> SkippedGroups,
    bool IsInUsersGroup);
