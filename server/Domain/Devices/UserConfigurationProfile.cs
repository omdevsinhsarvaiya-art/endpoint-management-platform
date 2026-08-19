using System.Collections.Frozen;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// What kind of local account is being asked for. Administrator is real Windows
/// group membership, never a stored flag.
/// </summary>
public enum LocalAccountType
{
    /// <summary>An ordinary account with no membership of BUILTIN\Administrators.</summary>
    StandardUser = 0,

    /// <summary>An account that is a member of BUILTIN\Administrators.</summary>
    Administrator = 1,
}

/// <summary>
/// A reusable baseline for creating local Windows accounts.
/// </summary>
/// <remarks>
/// <para>
/// A profile is a <em>template</em>, not an authority. It supplies sensible defaults
/// so an operator does not hand-assemble the same settings for every hire, but it
/// grants nothing: permission, device scope, the safety rules and explicit
/// confirmation are all still enforced afterwards. Selecting the "IT Administrator"
/// profile does not let an operator who lacks
/// <see cref="Authorization.Permissions.LocalUser.ChangeType"/> mint an administrator.
/// </para>
/// <para>
/// Profiles are defined in code rather than stored as data, matching how permissions
/// and system roles are already handled: a baseline that decides whether an account
/// gets administrator rights is a reviewable decision, not a row someone can edit.
/// </para>
/// </remarks>
public sealed record UserConfigurationProfile(
    string Key,
    string DisplayName,
    string Description,
    LocalAccountType AccountType,
    bool Enabled,
    bool MustChangePasswordAtNextLogon,
    IReadOnlyList<string> AdditionalGroups)
{
    /// <summary>True when this profile asks for administrator rights.</summary>
    public bool GrantsAdministrator => AccountType == LocalAccountType.Administrator;
}

/// <summary>The built-in baselines, and the rules about which groups may be requested.</summary>
public static class UserConfigurationProfiles
{
    public const string StandardEmployee = "standard_employee";
    public const string ItAdministrator = "it_administrator";

    /// <summary>
    /// Local groups an operator may add a new account to during creation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allow-list, not a deny-list: a group absent from here cannot be requested,
    /// so a group added to Windows in future is unreachable until someone reviews it.
    /// The inverse would silently expose every new privileged group.
    /// </para>
    /// <para>
    /// This is a policy ceiling, not a claim that these groups exist. Which of them a
    /// given machine actually has varies by Windows SKU, so what an operator is offered
    /// is this list intersected with that device's reported groups
    /// (<see cref="PermittedGroupsPresentOn"/>).
    /// </para>
    /// <para>
    /// <c>Administrators</c> is deliberately absent. Administrator rights are granted
    /// only through <see cref="LocalAccountType.Administrator"/>, which carries its
    /// own permission check, confirmation and last-administrator safety rules. Routing
    /// it through "additional groups" would be a way around all of that.
    /// </para>
    /// </remarks>
    public static readonly FrozenSet<string> PermittedAdditionalGroups =
        new[]
        {
            "Users",
            "Remote Desktop Users",
            "Backup Operators",
            "Performance Log Users",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Groups that may never be requested at creation time, whatever a profile says.</summary>
    public static readonly FrozenSet<string> ProtectedGroups =
        new[]
        {
            "Administrators",
            "Power Users",
            "Distributed COM Users",
            "Cryptographic Operators",
            "Hyper-V Administrators",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenDictionary<string, UserConfigurationProfile> All =
        new UserConfigurationProfile[]
        {
            new(
                StandardEmployee,
                "Standard Employee",
                "An ordinary user account with no administrative rights. The default for staff.",
                LocalAccountType.StandardUser,
                Enabled: true,
                MustChangePasswordAtNextLogon: true,
                AdditionalGroups: []),

            new(
                ItAdministrator,
                "IT Administrator",
                "An account with local administrator rights, for IT staff who support this device.",
                LocalAccountType.Administrator,
                Enabled: true,
                MustChangePasswordAtNextLogon: true,

                // Deliberately empty. A baseline must apply on every Windows SKU, and
                // optional groups do not: Home editions have no "Remote Desktop Users"
                // and no "Backup Operators". A profile that names one turns "create an
                // IT administrator" into a failure on those machines for a group that
                // was never the point. Administrator rights come from AccountType
                // above; anything beyond that is a per-device choice the operator makes
                // from the groups that device actually has.
                AdditionalGroups: []),
        }.ToFrozenDictionary(p => p.Key, StringComparer.Ordinal);

    /// <summary>
    /// The groups an operator may actually be offered for one device: the policy
    /// allow-list intersected with the groups that device last reported.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the API or the dashboard so the policy and the
    /// "does it exist" filter cannot drift apart. When a device has never reported
    /// its groups the full allow-list is returned: an empty inventory is missing
    /// knowledge, not evidence that the machine has no groups, and offering nothing
    /// would be its own wrong answer. The device is the final authority either way —
    /// it applies what it can and reports back what it did.
    /// </remarks>
    public static IReadOnlyList<string> PermittedGroupsPresentOn(IEnumerable<string>? deviceGroupNames)
    {
        var ordered = PermittedAdditionalGroups.Order(StringComparer.OrdinalIgnoreCase).ToList();

        var present = deviceGroupNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (present is null || present.Count == 0)
        {
            return ordered;
        }

        return ordered.Where(present.Contains).ToList();
    }

    public static UserConfigurationProfile? Find(string? key) =>
        key is not null && All.TryGetValue(key, out var profile) ? profile : null;

    /// <summary>
    /// Returns a refusal reason for a requested group, or null when it is allowed.
    /// Protected groups are named explicitly so the operator learns why, rather than
    /// getting a generic "not permitted".
    /// </summary>
    public static string? ValidateAdditionalGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return "A group name cannot be blank.";
        }

        if (ProtectedGroups.Contains(group))
        {
            return string.Equals(group, "Administrators", StringComparison.OrdinalIgnoreCase)
                ? "Administrator rights are granted through the account type, not through additional groups, "
                  + "so that the permission check and last-administrator safeguards always apply."
                : $"'{group}' is a protected Windows group and cannot be assigned when creating an account.";
        }

        return PermittedAdditionalGroups.Contains(group)
            ? null
            : $"'{group}' is not in the list of groups that may be assigned at account creation.";
    }
}
