using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Principal;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads local users, groups and membership through
/// <see cref="System.DirectoryServices.AccountManagement"/> — the typed local-SAM
/// API. No process launch, no command strings (ADR-0005), and nothing here can
/// mutate: only Principal read accessors are used.
/// </summary>
/// <remarks>
/// <para>
/// SIDs are the identity carried upstream; names are display data (accounts can
/// be renamed). Local-administrator status is computed by SID membership of the
/// well-known Administrators group (S-1-5-32-544) rather than by the group's
/// localised name, so it is correct on non-English Windows.
/// </para>
/// <para>
/// Reads work unelevated. Individual principals that fail to materialise
/// (orphaned domain members are common) are skipped with a debug log, never
/// allowed to fail the snapshot.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsLocalAccountsCollector(ILogger<WindowsLocalAccountsCollector> logger)
    : ILocalAccountsCollector
{
    private static readonly SecurityIdentifier AdministratorsGroupSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly ILogger<WindowsLocalAccountsCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public ValueTask<InventoryLocalAccounts> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var machineContext = new PrincipalContext(ContextType.Machine);

        var administratorSids = CollectAdministratorMemberSids(machineContext);
        var users = CollectUsers(machineContext, administratorSids);
        var groups = CollectGroups(machineContext);

        return ValueTask.FromResult(new InventoryLocalAccounts(users, groups));
    }

    /// <summary>SIDs of direct members of BUILTIN\Administrators, for the per-user flag.</summary>
    private HashSet<string> CollectAdministratorMemberSids(PrincipalContext machineContext)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var administrators = GroupPrincipal.FindByIdentity(
                machineContext, IdentityType.Sid, AdministratorsGroupSid.Value);

            if (administrators is null)
            {
                return sids;
            }

            foreach (var member in administrators.Members)
            {
                using (member)
                {
                    if (member.Sid is { } sid)
                    {
                        sids.Add(sid.Value);
                    }
                }
            }
        }
        catch (PrincipalException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate the Administrators group; the per-user "
                                   + "administrator flag will be false for all accounts in this snapshot.");
        }

        return sids;
    }

    private List<InventoryLocalUser> CollectUsers(PrincipalContext machineContext, HashSet<string> administratorSids)
    {
        var users = new List<InventoryLocalUser>();

        try
        {
            using var searcher = new PrincipalSearcher(new UserPrincipal(machineContext));
            using var results = searcher.FindAll();

            foreach (var principal in results)
            {
                using (principal)
                {
                    if (principal is not UserPrincipal user || user.Sid is null)
                    {
                        continue;
                    }

                    try
                    {
                        users.Add(new InventoryLocalUser(
                            user.Sid.Value,
                            user.SamAccountName ?? user.Name ?? user.Sid.Value,
                            NullIfEmpty(user.DisplayName),
                            NullIfEmpty(user.Description),
                            user.Enabled ?? false,
                            PasswordRequired: !user.PasswordNotRequired,
                            PasswordExpires: !user.PasswordNeverExpires,
                            user.LastLogon is { } lastLogon
                                ? new DateTimeOffset(DateTime.SpecifyKind(lastLogon, DateTimeKind.Utc))
                                : null,
                            administratorSids.Contains(user.Sid.Value)));
                    }
                    catch (PrincipalException ex)
                    {
                        _logger.LogDebug(ex, "Skipping unreadable local user principal.");
                    }
                }
            }
        }
        catch (PrincipalException ex)
        {
            _logger.LogWarning(ex, "Local user enumeration failed; reporting no users this snapshot.");
        }

        return users;
    }

    private List<InventoryLocalGroup> CollectGroups(PrincipalContext machineContext)
    {
        var groups = new List<InventoryLocalGroup>();

        try
        {
            using var searcher = new PrincipalSearcher(new GroupPrincipal(machineContext));
            using var results = searcher.FindAll();

            foreach (var principal in results)
            {
                using (principal)
                {
                    if (principal is not GroupPrincipal group || group.Sid is null)
                    {
                        continue;
                    }

                    groups.Add(new InventoryLocalGroup(
                        group.Sid.Value,
                        group.SamAccountName ?? group.Name ?? group.Sid.Value,
                        NullIfEmpty(group.Description),
                        CollectMembers(group)));
                }
            }
        }
        catch (PrincipalException ex)
        {
            _logger.LogWarning(ex, "Local group enumeration failed; reporting no groups this snapshot.");
        }

        return groups;
    }

    private List<InventoryGroupMember> CollectMembers(GroupPrincipal group)
    {
        var members = new List<InventoryGroupMember>();

        try
        {
            foreach (var member in group.Members)
            {
                using (member)
                {
                    members.Add(new InventoryGroupMember(
                        member.SamAccountName ?? member.Name ?? member.Sid?.Value ?? "(unknown)",
                        member.Sid?.Value,
                        member switch
                        {
                            UserPrincipal => "User",
                            GroupPrincipal => "Group",
                            _ => "Other",
                        }));
                }
            }
        }
        catch (PrincipalException ex)
        {
            // Groups containing orphaned domain SIDs throw here routinely on
            // workgroup machines; report the group with the members we got.
            _logger.LogDebug(ex, "Partial membership for group {Group}.", group.SamAccountName);
        }

        return members;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
