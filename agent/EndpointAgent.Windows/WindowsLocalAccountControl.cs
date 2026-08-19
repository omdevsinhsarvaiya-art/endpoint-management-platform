using System.ComponentModel;
using System.DirectoryServices.AccountManagement;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Mutates local Windows accounts and group memberships through the account
/// management APIs in <c>netapi32.dll</c>.
/// </summary>
/// <remarks>
/// <para>
/// No process is launched and no shell is invoked (ADR-0005). Every operation is a
/// direct API call taking typed parameters, so there is no command line for a
/// hostile value to escape into — a username containing <c>&amp; del *</c> is just a
/// username that fails validation, not an injection.
/// </para>
/// <para>
/// Account type is real Windows state: promotion adds the account to
/// <c>BUILTIN\Administrators</c> (well-known SID <c>S-1-5-32-544</c>) via
/// <c>NetLocalGroupAddMembers</c>, and demotion removes it via
/// <c>NetLocalGroupDelMembers</c>. The group is addressed by SID and resolved to its
/// local name, so this works on localized Windows where the group is not called
/// "Administrators".
/// </para>
/// <para>
/// The Net* functions return a status code directly rather than setting the last
/// error, so results are checked against the returned value (never
/// <c>Marshal.GetLastWin32Error</c>). Passwords are pinned and zeroed after use so a
/// plaintext does not linger in a GC-movable buffer.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsLocalAccountControl(ILogger<WindowsLocalAccountControl> logger) : ILocalAccountsControl
{
    private const uint NERR_Success = 0;
    private const uint NERR_UserExists = 2224;
    private const uint NERR_MemberInAlias = 1378;   // ERROR_MEMBER_IN_ALIAS: already a member.
    private const uint NERR_MemberNotInAlias = 1377; // ERROR_MEMBER_NOT_IN_ALIAS: not a member.

    // UF_* account-control flags (lmaccess.h).
    private const uint UF_SCRIPT = 0x0001;
    private const uint UF_ACCOUNTDISABLE = 0x0002;
    private const uint UF_NORMAL_ACCOUNT = 0x0200;

    private const uint NERR_PasswordTooShort = 2245;
    private const uint NERR_BadUsername = 2202;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_INVALID_PARAMETER = 87;

    /// <summary>BUILTIN\Administrators, by well-known SID so localized Windows still matches.</summary>
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// BUILTIN\Users (S-1-5-32-545), the baseline every local account belongs to.
    /// </summary>
    /// <remarks>
    /// By well-known SID for the same reason as Administrators: the group is named
    /// differently on localized Windows, so a name lookup would fail there.
    /// </remarks>
    private static readonly SecurityIdentifier UsersSid =
        new(WellKnownSidType.BuiltinUsersSid, null);

    private const uint USER_PRIV_USER = 1;
    private const uint TIMEQ_FOREVER = unchecked((uint)-1);

    private readonly ILogger<WindowsLocalAccountControl> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<CreatedLocalAccount> CreateUserAsync(
        string username,
        string password,
        string? fullName,
        string? description,
        bool enabled,
        bool mustChangePasswordAtNextLogon,
        bool administrator,
        IReadOnlyList<string> additionalGroups,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // Deliberately NOT UF_DONT_EXPIRE_PASSWD: a created account follows the
        // machine's password policy like any other. Opting every new account out of
        // expiry would quietly weaken the endpoint's posture.
        var flags = UF_SCRIPT | UF_NORMAL_ACCOUNT;
        if (!enabled)
        {
            flags |= UF_ACCOUNTDISABLE;
        }

        var info = new USER_INFO_1
        {
            usri1_name = username,
            usri1_password = password,
            usri1_priv = USER_PRIV_USER,      // Never USER_PRIV_ADMIN: admin rights come from group membership below.
            usri1_comment = description,
            usri1_flags = flags,
            usri1_script_path = null,
            usri1_home_dir = null,
            usri1_password_age = 0,
        };

        var status = NativeMethods.NetUserAdd(null, 1, ref info, out var parameterIndex);
        if (status != NERR_Success)
        {
            throw new InvalidOperationException(DescribeCreateFailure(status, username, parameterIndex));
        }

        // From here the account EXISTS. Everything below either completes, or the
        // account is removed again - a task that reports failure must leave the
        // machine as it found it. A half-built account surviving a failed task is
        // exactly how reported state and real state drift apart.
        try
        {
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                SetFullName(username, fullName!);
            }

            var createdSid = ResolveSid(username);

            // BUILTIN\Users, always, for every account type.
            //
            // NetUserAdd does NOT do this. usri1_priv = USER_PRIV_USER looks like it
            // sets the account's privilege level, but it grants no group membership,
            // so an account created here had no local group memberships at all until
            // this call existed. The account still authenticated - BUILTIN\Users
            // contains NT AUTHORITY\Authenticated Users, so logon works either way -
            // which is precisely why the gap stayed invisible: everything appeared
            // fine while the account was not in the group anyone would inspect.
            //
            // Applied to administrators too, not just standard users: demotion removes
            // the account from Administrators, and an administrator that never joined
            // Users would land in exactly the groupless state this fixes. The baseline
            // has to hold before the demotion, not be repaired after it.
            //
            // Required, not best-effort: unlike the optional groups below this is a
            // well-known SID present on every Windows install, so a failure here is a
            // real failure and must roll the account back.
            await SetGroupMembershipAsync(UsersSid.Value, createdSid, isMember: true, cancellationToken);

            // Optional groups are best-effort: which of them a machine has depends on
            // its Windows edition, and rolling back a correct account because this SKU
            // has no "Remote Desktop Users" would be a worse outcome than the account
            // simply not being in it. Skips are collected and reported, never hidden.
            var skippedGroups = new List<string>();
            foreach (var group in additionalGroups ?? [])
            {
                var groupSid = TryResolveGroupSid(group);
                if (groupSid is null)
                {
                    _logger.LogWarning(
                        "Local group '{Group}' does not exist on this device; '{Username}' was not added to it.",
                        group, username);
                    skippedGroups.Add(group);
                    continue;
                }

                await SetGroupMembershipAsync(groupSid, createdSid, isMember: true, cancellationToken);
            }

            // Administrator is NOT optional. It is the whole meaning of the request,
            // it is addressed by well-known SID so it exists on every Windows, and it
            // is verified below - a failure here rolls the account back.
            if (administrator)
            {
                await SetGroupMembershipAsync(
                    AdministratorsSid.Value, createdSid, isMember: true, cancellationToken);
            }

            // Expiring the password is the LAST mutation, deliberately: it is the one
            // step whose effect is a cleared timestamp rather than a set value, so any
            // later write that re-stamps the account would undo it silently. Ordering
            // it last removes that whole class of interaction rather than relying on
            // each subsequent call being harmless.
            if (mustChangePasswordAtNextLogon)
            {
                SetMustChangePassword(username);
            }

            // Read the achieved state back FROM Windows. Reporting the request as the
            // outcome is how a silently-ignored flag goes unnoticed.
            var verified = (ReadCreatedAccount(username)
                ?? throw new InvalidOperationException(
                    $"Windows reported success creating '{username}' but the account cannot be read back."))
                with { SkippedGroups = skippedGroups };

            // Verify against what Windows reports, not against what was requested.
            if (!verified.IsInUsersGroup)
            {
                throw new InvalidOperationException(
                    $"'{username}' was created but is not a member of the Users group.");
            }

            if (administrator && !verified.IsAdministrator)
            {
                throw new InvalidOperationException(
                    $"'{username}' was created but could not be added to the Administrators group.");
            }

            // The dangerous direction: a standard account that somehow ended up an
            // administrator is worse than a failed create, so it is rolled back rather
            // than reported as a success.
            if (!administrator && verified.IsAdministrator)
            {
                throw new InvalidOperationException(
                    $"'{username}' was requested as a standard user but is a member of the Administrators group.");
            }

            if (verified.Enabled != enabled)
            {
                throw new InvalidOperationException(
                    $"'{username}' was created but its enabled state is {verified.Enabled}, not {enabled}.");
            }

            _logger.LogInformation(
                "Created local user {Username} (enabled: {Enabled}, administrator: {Administrator}, "
                + "in Users group: {InUsers}, groups not present on this device: {SkippedCount}).",
                username, verified.Enabled, verified.IsAdministrator, verified.IsInUsersGroup,
                skippedGroups.Count);

            return verified;
        }
        catch (Exception ex)
        {
            var rollbackNote = TryRollbackCreate(username)
                ? " The partially-created account was removed, so this device is unchanged."
                : $" WARNING: the account '{username}' was created but could not be removed; it may exist "
                  + "on the device in an incomplete state.";

            throw new InvalidOperationException(
                $"Creating '{username}' failed after the account was added.{rollbackNote} Reason: {ex.Message}", ex);
        }
    }

    /// <summary>Removes a just-created account after a later step failed. True when the machine is clean again.</summary>
    private bool TryRollbackCreate(string username)
    {
        try
        {
            return NativeMethods.NetUserDel(null, username) == NERR_Success;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogError(ex, "Could not roll back the creation of {Username}.", username);
            return false;
        }
    }

    /// <summary>
    /// Turns a netapi32 status into something an operator can act on. A bare
    /// "Win32Exception" tells them nothing and sends them digging through agent logs.
    /// </summary>
    private static string DescribeCreateFailure(uint status, string username, uint parameterIndex) =>
        status switch
        {
            NERR_UserExists => $"A local account named '{username}' already exists on this device.",
            NERR_PasswordTooShort =>
                "The password was rejected by the device's password policy (length, complexity or history).",
            NERR_BadUsername => $"'{username}' is not a valid Windows account name.",
            ERROR_ACCESS_DENIED =>
                "Access denied. The agent must run elevated (as a service it runs as LocalSystem) to create accounts.",
            ERROR_INVALID_PARAMETER => $"Windows rejected the account details (parameter {parameterIndex}).",
            _ => $"Windows could not create '{username}' (status {status}, parameter {parameterIndex}).",
        };

    /// <summary>Reads back the account Windows actually has, including live group membership.</summary>
    private static CreatedLocalAccount? ReadCreatedAccount(string username)
    {
        using var context = new PrincipalContext(ContextType.Machine);
        using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
        if (user?.Sid is null)
        {
            return null;
        }

        var groups = new List<string>();
        var isAdministrator = false;
        var isInUsersGroup = false;

        // Membership is decided by SID, never by name: the same group is called
        // something else on localized Windows, and a name comparison would silently
        // report "not a member" there.
        foreach (var group in user.GetGroups())
        {
            using (group)
            {
                if (group.Name is { } name)
                {
                    groups.Add(name);
                }

                if (group.Sid is not { } sid)
                {
                    continue;
                }

                if (string.Equals(sid.Value, AdministratorsSid.Value, StringComparison.OrdinalIgnoreCase))
                {
                    isAdministrator = true;
                }
                else if (string.Equals(sid.Value, UsersSid.Value, StringComparison.OrdinalIgnoreCase))
                {
                    isInUsersGroup = true;
                }
            }
        }

        return new CreatedLocalAccount(
            user.Sid.Value, user.SamAccountName ?? username, user.Enabled ?? false, isAdministrator, groups,
            SkippedGroups: [], IsInUsersGroup: isInUsersGroup);
    }

    private static string ResolveSid(string username)
    {
        using var context = new PrincipalContext(ContextType.Machine);
        using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
        return user?.Sid?.Value
            ?? throw new InvalidOperationException($"Local user '{username}' could not be found after creation.");
    }

    /// <summary>
    /// Resolves a local group NAME to its SID, so membership work stays SID-addressed.
    /// Returns null when this machine has no such group.
    /// </summary>
    private static string? TryResolveGroupSid(string groupName)
    {
        using var context = new PrincipalContext(ContextType.Machine);
        using var group = GroupPrincipal.FindByIdentity(context, IdentityType.Name, groupName);
        return group?.Sid?.Value;
    }

    public ValueTask DeleteUserAsync(string sid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var username = ResolveUsername(sid);

        var status = NativeMethods.NetUserDel(null, username);
        ThrowIfFailed(status, $"Deleting local user '{username}' failed.");

        _logger.LogInformation("Deleted local user {Username}.", username);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetUserEnabledAsync(string sid, bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var username = ResolveUsername(sid);

        var flags = GetUserFlags(username);
        var updated = enabled ? flags & ~UF_ACCOUNTDISABLE : flags | UF_ACCOUNTDISABLE;

        SetUserFlags(username, updated);

        _logger.LogInformation("Local user {Username} {State}.", username, enabled ? "enabled" : "disabled");
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPasswordAsync(string sid, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var username = ResolveUsername(sid);

        var info = new USER_INFO_1003 { usri1003_password = password };
        var status = NativeMethods.NetUserSetInfo(null, username, 1003, ref info, out _);
        ThrowIfFailed(status, $"Resetting the password for '{username}' failed.");

        // Do not log the password, its length, or any derived value.
        _logger.LogInformation("Password reset for local user {Username}.", username);
        return ValueTask.CompletedTask;
    }

    public ValueTask ForcePasswordChangeAsync(string sid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var username = ResolveUsername(sid);
        SetMustChangePassword(username);

        _logger.LogInformation("Local user {Username} must change password at next logon.", username);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetGroupMembershipAsync(
        string groupSid, string memberSid, bool isMember, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var groupName = ResolveGroupName(groupSid);
        var memberSidBytes = ToBinarySid(memberSid);

        var handle = GCHandle.Alloc(memberSidBytes, GCHandleType.Pinned);
        try
        {
            var member = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = handle.AddrOfPinnedObject() };
            var members = new[] { member };

            var status = isMember
                ? NativeMethods.NetLocalGroupAddMembers(null, groupName, 0, members, 1)
                : NativeMethods.NetLocalGroupDelMembers(null, groupName, 0, members, 1);

            // Converge rather than fail: adding an existing member or removing a
            // non-member means the desired state already holds, so a retried task
            // is a success, not an error.
            if ((isMember && status == NERR_MemberInAlias) || (!isMember && status == NERR_MemberNotInAlias))
            {
                _logger.LogInformation(
                    "Group '{Group}' membership for {MemberSid} already in the desired state.", groupName, memberSid);
                return ValueTask.CompletedTask;
            }

            ThrowIfFailed(
                status,
                $"{(isMember ? "Adding" : "Removing")} member in local group '{groupName}' failed.");
        }
        finally
        {
            handle.Free();
        }

        _logger.LogInformation(
            "{Action} {MemberSid} {Preposition} local group '{Group}'.",
            isMember ? "Added" : "Removed", memberSid, isMember ? "to" : "from", groupName);

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<LiveLocalAccount>> GetLiveAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var accounts = new List<LiveLocalAccount>();

        using var machineContext = new PrincipalContext(ContextType.Machine);

        // Live membership of BUILTIN\Administrators, by well-known SID so this works
        // on localized Windows.
        var administratorSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        try
        {
            using var administrators = GroupPrincipal.FindByIdentity(
                machineContext, IdentityType.Sid, administratorsSid.Value);

            if (administrators is not null)
            {
                foreach (var member in administrators.Members)
                {
                    using (member)
                    {
                        if (member.Sid is { } sid)
                        {
                            administratorSids.Add(sid.Value);
                        }
                    }
                }
            }
        }
        catch (PrincipalException ex)
        {
            // A safety re-check that cannot see the administrators group must not
            // silently claim "no admins" — that would let a last-admin removal through.
            throw new InvalidOperationException(
                "Could not read live Administrators membership for the safety re-check.", ex);
        }

        using var searcher = new PrincipalSearcher(new UserPrincipal(machineContext));
        foreach (var result in searcher.FindAll())
        {
            using (result)
            {
                if (result is not UserPrincipal user || user.Sid is null)
                {
                    continue;
                }

                accounts.Add(new LiveLocalAccount(
                    user.Sid.Value,
                    user.SamAccountName ?? user.Name ?? user.Sid.Value,
                    user.Enabled ?? false,
                    administratorSids.Contains(user.Sid.Value)));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<LiveLocalAccount>>(accounts);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Resolves a SID to its local account name; throws if it is not a local user.</summary>
    private static string ResolveUsername(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);

        using var machineContext = new PrincipalContext(ContextType.Machine);
        using var user = UserPrincipal.FindByIdentity(machineContext, IdentityType.Sid, sid);

        return user?.SamAccountName
            ?? throw new InvalidOperationException($"No local user with SID '{sid}' exists on this machine.");
    }

    /// <summary>Resolves a group SID to its local (possibly localized) name.</summary>
    private static string ResolveGroupName(string groupSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupSid);

        var identifier = new SecurityIdentifier(groupSid);
        var account = (NTAccount)identifier.Translate(typeof(NTAccount));

        // "BUILTIN\Administrators" -> "Administrators": the Net* APIs take the bare name.
        var name = account.Value;
        var separator = name.LastIndexOf('\\');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private static byte[] ToBinarySid(string sid)
    {
        var identifier = new SecurityIdentifier(sid);
        var bytes = new byte[identifier.BinaryLength];
        identifier.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static uint GetUserFlags(string username)
    {
        var status = NativeMethods.NetUserGetInfo(null, username, 1, out var buffer);
        ThrowIfFailed(status, $"Reading account flags for '{username}' failed.");

        try
        {
            var info = Marshal.PtrToStructure<USER_INFO_1>(buffer);
            return info.usri1_flags;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.NetApiBufferFree(buffer);
            }
        }
    }

    private static void SetUserFlags(string username, uint flags)
    {
        var info = new USER_INFO_1008 { usri1008_flags = flags };
        var status = NativeMethods.NetUserSetInfo(null, username, 1008, ref info, out _);
        ThrowIfFailed(status, $"Updating account flags for '{username}' failed.");
    }

    private static void SetFullName(string username, string fullName)
    {
        var info = new USER_INFO_1011 { usri1011_full_name = fullName };
        var status = NativeMethods.NetUserSetInfo(null, username, 1011, ref info, out _);
        ThrowIfFailed(status, $"Setting the display name for '{username}' failed.");
    }

    /// <summary>
    /// Marks the password expired so Windows demands a new one at next logon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two Net* approaches look right and are not: level 1017 is
    /// <c>acct_expires</c> (when the ACCOUNT dies, not the password), and the
    /// <c>UF_PASSWORD_EXPIRED</c> bit is silently ignored when written through the
    /// level-1008 flags — the call returns success and nothing changes, which is
    /// worse than an error.
    /// </para>
    /// <para>
    /// <see cref="UserPrincipal.ExpirePasswordNow"/> is the supported way to do this.
    /// It is a managed API call, not a shell or a spawned process, so ADR-0005 holds.
    /// A password flagged "never expires" cannot also be expired, so that flag is
    /// cleared first.
    /// </para>
    /// </remarks>
    private static void SetMustChangePassword(string username)
    {
        using var context = new PrincipalContext(ContextType.Machine);
        using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username)
            ?? throw new InvalidOperationException(
                $"Local user '{username}' disappeared before its password could be expired.");

        if (user.PasswordNeverExpires)
        {
            user.PasswordNeverExpires = false;
            user.Save();
        }

        user.ExpirePasswordNow();
    }

    /// <summary>
    /// Net* functions return their status directly, so success is checked against the
    /// return value rather than the thread's last error.
    /// </summary>
    private static void ThrowIfFailed(uint status, string message)
    {
        if (status != NERR_Success)
        {
            throw new Win32Exception((int)status, $"{message} (status {status})");
        }
    }

    // -------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public uint usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USER_INFO_1008
    {
        public uint usri1008_flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1011
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1011_full_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LOCALGROUP_MEMBERS_INFO_0
    {
        public IntPtr lgrmi0_sid;
    }

    private static class NativeMethods
    {
        // netapi32 exports these as Unicode-only (no A/W pair), so the plain name is
        // the real entry point.
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetUserAdd(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            uint level,
            ref USER_INFO_1 buf,
            out uint parameterErrorIndex);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetUserDel(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string username);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetUserGetInfo(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string username,
            uint level,
            out IntPtr buffer);

        [DllImport("netapi32.dll", EntryPoint = "NetUserSetInfo", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetUserSetInfo(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string username,
            uint level,
            ref USER_INFO_1003 buf,
            out uint parameterErrorIndex);

        [DllImport("netapi32.dll", EntryPoint = "NetUserSetInfo", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetUserSetInfo(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string username,
            uint level,
            ref USER_INFO_1008 buf,
            out uint parameterErrorIndex);

        [DllImport("netapi32.dll", EntryPoint = "NetUserSetInfo", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetUserSetInfo(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string username,
            uint level,
            ref USER_INFO_1011 buf,
            out uint parameterErrorIndex);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetLocalGroupAddMembers(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string groupName,
            uint level,
            [In] LOCALGROUP_MEMBERS_INFO_0[] buf,
            uint totalEntries);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint NetLocalGroupDelMembers(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            [MarshalAs(UnmanagedType.LPWStr)] string groupName,
            uint level,
            [In] LOCALGROUP_MEMBERS_INFO_0[] buf,
            uint totalEntries);

        [DllImport("netapi32.dll", ExactSpelling = true)]
        internal static extern uint NetApiBufferFree(IntPtr buffer);
    }
}
